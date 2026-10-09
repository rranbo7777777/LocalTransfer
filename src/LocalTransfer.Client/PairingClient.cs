using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LocalTransfer.Contracts.Devices;
using LocalTransfer.Contracts.Pairing;
using LocalTransfer.Contracts.Protocol;

namespace LocalTransfer.Client;

public static class PairingClient
{
    /// <summary>
    /// How many times a single request is attempted before the failure is surfaced. The status
    /// poll is rate limited per source address by the coordinator, so a 429 there is transient
    /// and must not abort a pairing the user is still answering on the desktop.
    /// </summary>
    private const int MaxAttempts = 4;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<CoordinatorConnection> PairAsync(
        PairingBootstrap bootstrap,
        DeviceDescriptor device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(device);
        if (bootstrap.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The pairing information has expired.");
        }

        if (bootstrap.ProtocolVersion < ProtocolConstants.MinimumSupportedVersion ||
            bootstrap.ProtocolVersion > ProtocolConstants.CurrentVersion)
        {
            throw new NotSupportedException("The coordinator protocol version is not supported.");
        }

        using var client = PinnedHttpClientFactory.Create(
            bootstrap.Endpoint,
            bootstrap.CertificateSha256);

        PairingSubmissionResponse submission;
        using (var submitResponse = await SendWithRetryAsync(
                   client,
                   () => new HttpRequestMessage(HttpMethod.Post, "/api/v1/pairing")
                   {
                       Content = JsonContent.Create(
                           new PairingSubmission(bootstrap.Secret, device),
                           options: SerializerOptions)
                   },
                   // The submission consumes a one-time ticket, so a retry is only safe when the
                   // request was rejected outright (429). Anything else may already have been
                   // processed, and replaying the ticket would then fail with 401.
                   isIdempotent: false,
                   cancellationToken))
        {
            submitResponse.EnsureSuccessStatusCode();
            submission = await BoundedJsonReader.ReadAsync<PairingSubmissionResponse>(
                    submitResponse.Content,
                    SerializerOptions,
                    cancellationToken)
                ?? throw new InvalidDataException("The coordinator returned an empty pairing response.");
        }

        while (DateTimeOffset.UtcNow < bootstrap.ExpiresAtUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pollResponse = await SendWithRetryAsync(
                client,
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    $"/api/v1/pairing/{submission.RequestId}"),
                isIdempotent: true,
                cancellationToken);
            pollResponse.EnsureSuccessStatusCode();
            var poll = await BoundedJsonReader.ReadAsync<PairingPollResponse>(
                pollResponse.Content,
                SerializerOptions,
                cancellationToken)
                ?? throw new InvalidDataException("The coordinator returned an empty pairing status.");

            if (poll.Status == PairingRequestStatus.Approved)
            {
                if (string.IsNullOrWhiteSpace(poll.Credential))
                {
                    throw new InvalidDataException("The approved pairing response did not contain a credential.");
                }

                return new CoordinatorConnection(
                    bootstrap.Endpoint,
                    bootstrap.CertificateSha256,
                    device.DeviceId,
                    poll.Credential);
            }

            if (poll.Status == PairingRequestStatus.Rejected)
            {
                throw new UnauthorizedAccessException("The computer rejected the pairing request.");
            }

            if (poll.Status == PairingRequestStatus.CredentialDelivered)
            {
                throw new InvalidOperationException("The one-time pairing credential was already delivered.");
            }

            await Task.Delay(ProtocolConstants.PairingPollInterval, cancellationToken);
        }

        throw new TimeoutException("The pairing request expired before it was approved.");
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> createRequest,
        bool isIdempotent,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = createRequest();
                var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (attempt < MaxAttempts && ShouldRetry(response.StatusCode, isIdempotent))
                {
                    response.Dispose();
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }

                return response;
            }
            catch (Exception exception) when (
                isIdempotent &&
                attempt < MaxAttempts &&
                !cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or TaskCanceledException)
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
        }
    }

    private static bool ShouldRetry(HttpStatusCode statusCode, bool isIdempotent) =>
        statusCode == HttpStatusCode.TooManyRequests ||
        (isIdempotent && (int)statusCode >= 500);

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
}
