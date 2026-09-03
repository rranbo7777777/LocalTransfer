using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LocalTransfer.Contracts.Devices;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Coordinator;
using LocalTransfer.Coordinator.Security;

namespace LocalTransfer.IntegrationTests;

public sealed class CoordinatorHostTests
{
    [Fact]
    public async Task HealthEndpoint_IsAvailableOverPinnedHttps()
    {
        using var directory = new CoordinatorTemporaryDirectory();
        var port = GetAvailablePort();
        var certificate = CreateCertificate();
        var expectedFingerprint = LocalCertificateProvider.GetSha256Fingerprint(certificate);
        var options = new CoordinatorOptions
        {
            ListenAddress = IPAddress.Loopback,
            Port = port,
            DataDirectory = System.IO.Path.Combine(directory.Path, "data"),
            ReceiveDirectory = System.IO.Path.Combine(directory.Path, "received")
        };
        await using var host = new CoordinatorHost(options, () => certificate);
        await host.StartAsync();

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, serverCertificate, _, _) =>
                serverCertificate is not null &&
                string.Equals(
                    expectedFingerprint,
                    LocalCertificateProvider.GetSha256Fingerprint(new X509Certificate2(serverCertificate)),
                    StringComparison.Ordinal)
        };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync($"{host.Endpoint}/api/v1/health");
        var content = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("LocalTransfer", content, StringComparison.Ordinal);
        Assert.Contains("\"protocolVersion\":1", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustedDevice_CanUploadChunksAfterComputerApproval()
    {
        using var directory = new CoordinatorTemporaryDirectory();
        var certificate = CreateCertificate();
        var options = new CoordinatorOptions
        {
            ListenAddress = IPAddress.Loopback,
            Port = GetAvailablePort(),
            DataDirectory = System.IO.Path.Combine(directory.Path, "data"),
            ReceiveDirectory = System.IO.Path.Combine(directory.Path, "received")
        };
        await using var host = new CoordinatorHost(options, () => certificate);
        var device = new DeviceDescriptor(
            Guid.NewGuid(),
            "集成测试手机",
            "Android",
            ProtocolConstants.CurrentVersion,
            DeviceCapabilities.Upload | DeviceCapabilities.Resume);
        const string credential = "integration-test-credential";
        await host.TrustedDevices.AddOrUpdateAsync(device, credential);
        await host.StartAsync();

        using var client = CreatePinnedClient(certificate);
        client.DefaultRequestHeaders.Add("X-LocalTransfer-Device", device.DeviceId.ToString());
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);

        var content = "通过 HTTPS 分块传输并完成哈希校验"u8.ToArray();
        var manifest = new FileManifest(
            Guid.NewGuid(),
            "测试文件.txt",
            content.Length,
            DateTimeOffset.UtcNow,
            7,
            Convert.ToHexString(SHA256.HashData(content)));

        var offerResponse = await client.PostAsJsonAsync($"{host.Endpoint}/api/v1/transfers", manifest);
        Assert.Equal(HttpStatusCode.Accepted, offerResponse.StatusCode);
        Assert.True(await host.InboundTransfers.ApproveAsync(manifest.TransferId));

        for (var index = 0; index < manifest.ChunkCount; index++)
        {
            var offset = index * manifest.ChunkSize;
            var length = Math.Min(manifest.ChunkSize, content.Length - offset);
            var chunk = content.AsMemory(offset, length).ToArray();
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"{host.Endpoint}/api/v1/transfers/{manifest.TransferId}/chunks/{index}")
            {
                Content = new ByteArrayContent(chunk)
            };
            request.Headers.Add("X-Chunk-SHA256", Convert.ToHexString(SHA256.HashData(chunk)));

            var chunkResponse = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, chunkResponse.StatusCode);
        }

        var completeResponse = await client.PostAsync(
            $"{host.Endpoint}/api/v1/transfers/{manifest.TransferId}/complete",
            content: null);
        completeResponse.EnsureSuccessStatusCode();

        var repeatedCompleteResponse = await client.PostAsync(
            $"{host.Endpoint}/api/v1/transfers/{manifest.TransferId}/complete",
            content: null);
        repeatedCompleteResponse.EnsureSuccessStatusCode();

        var savedPath = System.IO.Path.Combine(options.ReceiveDirectory, manifest.FileName);
        Assert.Equal(content, await File.ReadAllBytesAsync(savedPath));
        Assert.False(Directory.EnumerateFiles(
            System.IO.Path.Combine(options.ReceiveDirectory, ".localtransfer")).Any());
    }

    [Fact]
    public async Task TransferEndpoint_RejectsUntrustedDevice()
    {
        using var directory = new CoordinatorTemporaryDirectory();
        var certificate = CreateCertificate();
        var options = new CoordinatorOptions
        {
            ListenAddress = IPAddress.Loopback,
            Port = GetAvailablePort(),
            DataDirectory = System.IO.Path.Combine(directory.Path, "data"),
            ReceiveDirectory = System.IO.Path.Combine(directory.Path, "received")
        };
        await using var host = new CoordinatorHost(options, () => certificate);
        await host.StartAsync();

        using var client = CreatePinnedClient(certificate);
        client.DefaultRequestHeaders.Add("X-LocalTransfer-Device", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid");
        var content = Array.Empty<byte>();
        var manifest = new FileManifest(
            Guid.NewGuid(),
            "unauthorized.txt",
            0,
            DateTimeOffset.UtcNow,
            ProtocolConstants.DefaultChunkSize,
            Convert.ToHexString(SHA256.HashData(content)));

        var response = await client.PostAsJsonAsync($"{host.Endpoint}/api/v1/transfers", manifest);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.InboundTransfers.GetPendingOffers());
    }

    [Fact]
    public async Task TrustedDevice_CanDownloadComputerQueuedFileInChunks()
    {
        using var directory = new CoordinatorTemporaryDirectory();
        var certificate = CreateCertificate();
        var options = new CoordinatorOptions
        {
            ListenAddress = IPAddress.Loopback,
            Port = GetAvailablePort(),
            DataDirectory = System.IO.Path.Combine(directory.Path, "data"),
            ReceiveDirectory = System.IO.Path.Combine(directory.Path, "received")
        };
        await using var host = new CoordinatorHost(options, () => certificate);
        var device = new DeviceDescriptor(
            Guid.NewGuid(),
            "下载测试手机",
            "iOS",
            ProtocolConstants.CurrentVersion,
            DeviceCapabilities.Download | DeviceCapabilities.Resume);
        const string credential = "outbound-integration-credential";
        await host.TrustedDevices.AddOrUpdateAsync(device, credential);
        await host.StartAsync();

        var sourceContent = RandomNumberGenerator.GetBytes(ProtocolConstants.DefaultChunkSize + 97);
        var sourcePath = System.IO.Path.Combine(directory.Path, "电脑发送.bin");
        await File.WriteAllBytesAsync(sourcePath, sourceContent);
        var queued = await host.OutboundTransfers.EnqueueAsync(device.DeviceId, sourcePath);

        using var client = CreatePinnedClient(certificate);
        client.DefaultRequestHeaders.Add("X-LocalTransfer-Device", device.DeviceId.ToString());
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);

        var available = await client.GetFromJsonAsync<LocalTransfer.Coordinator.Transfers.OutboundTransferInfo[]>(
            $"{host.Endpoint}/api/v1/outbound");
        Assert.Single(available!);
        Assert.Equal(queued.TransferId, available![0].TransferId);

        await using var downloaded = new MemoryStream();
        for (var index = 0; index < queued.Manifest.ChunkCount; index++)
        {
            var response = await client.GetAsync(
                $"{host.Endpoint}/api/v1/outbound/{queued.TransferId}/chunks/{index}");
            response.EnsureSuccessStatusCode();
            var chunk = await response.Content.ReadAsByteArrayAsync();
            var declaredHash = response.Headers.GetValues("X-Chunk-SHA256").Single();
            Assert.Equal(Convert.ToHexString(SHA256.HashData(chunk)), declaredHash);
            await downloaded.WriteAsync(chunk);
        }

        Assert.Equal(sourceContent, downloaded.ToArray());
        Assert.Equal(queued.Manifest.Sha256Hex, Convert.ToHexString(SHA256.HashData(downloaded.ToArray())));

        var completeResponse = await client.PostAsync(
            $"{host.Endpoint}/api/v1/outbound/{queued.TransferId}/complete",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, completeResponse.StatusCode);
        var repeatedCompleteResponse = await client.PostAsync(
            $"{host.Endpoint}/api/v1/outbound/{queued.TransferId}/complete",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, repeatedCompleteResponse.StatusCode);
        Assert.Empty(host.OutboundTransfers.GetAvailable(device.DeviceId));
    }

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
        var request = new CertificateRequest("CN=LocalTransfer Tests", key, HashAlgorithmName.SHA256);
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(10));
        return new X509Certificate2(generated.Export(X509ContentType.Pfx));
    }

    private sealed class CoordinatorTemporaryDirectory : IDisposable
    {
        public CoordinatorTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LocalTransfer.CoordinatorTests.{Guid.NewGuid():N}");
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
