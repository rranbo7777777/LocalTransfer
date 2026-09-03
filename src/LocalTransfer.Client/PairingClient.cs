using System.Net.Http.Json;
using LocalTransfer.Contracts.Devices;
using LocalTransfer.Contracts.Pairing;
using LocalTransfer.Contracts.Protocol;

namespace LocalTransfer.Client;

public static class PairingClient
{
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
        using var submitResponse = await client.PostAsJsonAsync(
            "/api/v1/pairing",
            new PairingSubmission(bootstrap.Secret, device),
            cancellationToken);
        submitResponse.EnsureSuccessStatusCode();
        var submission = await submitResponse.Content.ReadFromJsonAsync<PairingSubmissionResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("The coordinator returned an empty pairing response.");

        while (DateTimeOffset.UtcNow < bootstrap.ExpiresAtUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pollResponse = await client.GetAsync(
                $"/api/v1/pairing/{submission.RequestId}",
                cancellationToken);
            pollResponse.EnsureSuccessStatusCode();
            var poll = await pollResponse.Content.ReadFromJsonAsync<PairingPollResponse>(
                cancellationToken: cancellationToken)
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

            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
        }

        throw new TimeoutException("The pairing request expired before it was approved.");
    }
}
