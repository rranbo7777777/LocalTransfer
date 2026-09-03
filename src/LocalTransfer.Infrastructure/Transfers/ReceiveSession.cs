using LocalTransfer.Contracts.Transfers;

namespace LocalTransfer.Infrastructure.Transfers;

public sealed class ReceiveSession : IAsyncDisposable
{
    internal ReceiveSession(
        FileManifest manifest,
        string destinationDirectory,
        string temporaryPath,
        string checkpointPath,
        TransferCheckpoint checkpoint)
    {
        Manifest = manifest;
        DestinationDirectory = destinationDirectory;
        TemporaryPath = temporaryPath;
        CheckpointPath = checkpointPath;
        Checkpoint = checkpoint;
    }

    public FileManifest Manifest { get; }

    public string DestinationDirectory { get; }

    public string TemporaryPath { get; }

    internal string CheckpointPath { get; }

    internal TransferCheckpoint Checkpoint { get; set; }

    internal SemaphoreSlim Gate { get; } = new(1, 1);

    public ValueTask DisposeAsync()
    {
        Gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
