namespace LocalTransfer.Client;

public sealed record CoordinatorConnection(
    string Endpoint,
    string CertificateSha256,
    Guid DeviceId,
    string Credential);

public sealed record TransferProgress(
    Guid TransferId,
    string FileName,
    long BytesTransferred,
    long TotalBytes);
