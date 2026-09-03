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

public sealed record PairingRequestInfo(
    Guid RequestId,
    DeviceDescriptor Device,
    DateTimeOffset RequestedAtUtc,
    PairingRequestStatus Status);
