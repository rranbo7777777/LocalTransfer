using LocalTransfer.Contracts.Devices;

namespace LocalTransfer.Contracts.Pairing;

public enum PairingRequestStatus
{
    Pending,
    Approved,
    CredentialDelivered,
    Rejected
}

public sealed record PairingBootstrap(
    string Endpoint,
    string CertificateSha256,
    int ProtocolVersion,
    string Secret,
    DateTimeOffset ExpiresAtUtc);

public sealed record PairingSubmission(string Secret, DeviceDescriptor Device);

public sealed record PairingSubmissionResponse(Guid RequestId, PairingRequestStatus Status);

public sealed record PairingPollResponse(PairingRequestStatus Status, string? Credential);

/// <summary>
/// A pairing request waiting on the desktop. <paramref name="ExpiresAtUtc"/> is when the phone
/// stops waiting, so the approval UI can show a countdown instead of letting the user answer
/// after the client already gave up — which used to leave the two sides permanently out of sync.
/// </summary>
public sealed record PairingRequestInfo(
    Guid RequestId,
    DeviceDescriptor Device,
    DateTimeOffset RequestedAtUtc,
    PairingRequestStatus Status,
    DateTimeOffset ExpiresAtUtc = default);
