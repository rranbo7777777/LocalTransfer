using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LocalTransfer.Contracts.Devices;
using LocalTransfer.Contracts.Pairing;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Coordinator;
using LocalTransfer.Coordinator.Security;

namespace LocalTransfer.IntegrationTests;

/// <summary>
/// Guards the pairing rate limiter against the client's own polling pattern. The submit and poll
/// endpoints previously shared a single 30 requests/minute budget, so a legitimate client burned
/// through it after ~22 seconds and aborted every pairing the desktop user did not answer
/// immediately with HTTP 429.
/// </summary>
public sealed class PairingRateLimitTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PollEndpoint_SurvivesAClientsFullMinuteOfPolling()
    {
        using var directory = new PairingTemporaryDirectory();
        var certificate = CreateCertificate();
        await using var host = CreateHost(directory, certificate);
        await host.StartAsync();
        using var client = CreatePinnedClient(certificate);

        var requestId = await SubmitPairingRequestAsync(client, host, host.CreatePairingBootstrap());

        // A client polls for as long as the desktop dialog stays open, so a minute of polling must
        // never be throttled. Double the client's own per-minute rate to keep headroom for retries.
        var pollsPerMinute = (int)Math.Ceiling(TimeSpan.FromMinutes(1) / ProtocolConstants.PairingPollInterval);
        Assert.True(
            pollsPerMinute < CoordinatorHost.PairingPollPermitLimit,
            $"The client polls {pollsPerMinute} times per minute but the server only permits "
            + $"{CoordinatorHost.PairingPollPermitLimit}; a slow human would trip the limiter.");

        for (var attempt = 1; attempt <= pollsPerMinute * 2; attempt++)
        {
            using var response = await client.GetAsync($"{host.Endpoint}/api/v1/pairing/{requestId}");
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"Poll #{attempt} returned {(int)response.StatusCode} instead of 200; "
                + "the pairing poll is sharing a budget with the ticket submission again.");
        }
    }

    [Fact]
    public async Task SubmitEndpoint_StillThrottlesTicketFlooding_WithoutSpendingPollPermits()
    {
        using var directory = new PairingTemporaryDirectory();
        var certificate = CreateCertificate();
        await using var host = CreateHost(directory, certificate);
        await host.StartAsync();
        using var client = CreatePinnedClient(certificate);

        var device = new DeviceDescriptor(
            Guid.NewGuid(),
            "限流测试手机",
            "Android",
            ProtocolConstants.CurrentVersion,
            DeviceCapabilities.Upload | DeviceCapabilities.Resume);

        // The ticket is bogus so every submission is rejected, but each one still spends a permit.
        for (var attempt = 1; attempt <= CoordinatorHost.PairingSubmitPermitLimit; attempt++)
        {
            using var response = await SubmitAsync(client, host, device, "bogus-ticket");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var throttled = await SubmitAsync(client, host, device, "bogus-ticket");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // Flooding the submission endpoint must not consume the poll budget that a legitimate
        // client needs in order to observe the approval it is still waiting for.
        using var poll = await client.GetAsync($"{host.Endpoint}/api/v1/pairing/{Guid.NewGuid()}");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, poll.StatusCode);
    }

    private static async Task<Guid> SubmitPairingRequestAsync(
        HttpClient client,
        CoordinatorHost host,
        PairingBootstrap bootstrap)
    {
        var device = new DeviceDescriptor(
            Guid.NewGuid(),
            "轮询预算测试手机",
            "Android",
            ProtocolConstants.CurrentVersion,
            DeviceCapabilities.Upload | DeviceCapabilities.Resume);

        using var response = await SubmitAsync(client, host, device, bootstrap.Secret);
        response.EnsureSuccessStatusCode();
        var submission = await response.Content.ReadFromJsonAsync<PairingSubmissionResponse>(SerializerOptions);
        Assert.NotNull(submission);
        return submission!.RequestId;
    }

    private static Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        CoordinatorHost host,
        DeviceDescriptor device,
        string secret) =>
        client.PostAsJsonAsync(
            $"{host.Endpoint}/api/v1/pairing",
            new PairingSubmission(secret, device),
            SerializerOptions);

    private static CoordinatorHost CreateHost(PairingTemporaryDirectory directory, X509Certificate2 certificate) =>
        new(
            new CoordinatorOptions
            {
                ListenAddress = IPAddress.Loopback,
                Port = GetAvailablePort(),
                DataDirectory = Path.Combine(directory.Path, "data"),
                ReceiveDirectory = Path.Combine(directory.Path, "received")
            },
            () => certificate);

    private static HttpClient CreatePinnedClient(X509Certificate2 certificate)
    {
        var expectedFingerprint = LocalCertificateProvider.GetSha256Fingerprint(certificate);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, serverCertificate, _, _) =>
                serverCertificate is not null &&
                string.Equals(
                    expectedFingerprint,
                    LocalCertificateProvider.GetSha256Fingerprint(new X509Certificate2(serverCertificate)),
                    StringComparison.Ordinal)
        };
        return new HttpClient(handler);
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=LocalTransfer RateLimit Tests", key, HashAlgorithmName.SHA256);
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(10));
        return new X509Certificate2(generated.Export(X509ContentType.Pfx));
    }

    private sealed class PairingTemporaryDirectory : IDisposable
    {
        public PairingTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LocalTransfer.RateLimitTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
