using LocalTransfer.Contracts.Transfers;

namespace LocalTransfer.Client;

public sealed record RemoteInboundTransferInfo(
    Guid TransferId,
    Guid DeviceId,
    FileManifest Manifest,
    TransferState State,
    IReadOnlyList<int> MissingChunks,
    string? Error);

public sealed record RemoteOutboundTransferInfo(
    Guid TransferId,
    Guid DeviceId,
    FileManifest Manifest,
    TransferState State,
    string? Error);
