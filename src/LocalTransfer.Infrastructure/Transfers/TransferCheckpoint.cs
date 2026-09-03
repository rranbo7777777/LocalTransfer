using LocalTransfer.Contracts.Transfers;

namespace LocalTransfer.Infrastructure.Transfers;

internal sealed record TransferCheckpoint(
    FileManifest Manifest,
    string FinalFileName,
    HashSet<int> CompletedChunks);
