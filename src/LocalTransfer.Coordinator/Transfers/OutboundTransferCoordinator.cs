using System.Collections.Concurrent;
using System.Security.Cryptography;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Core.Transfers;

namespace LocalTransfer.Coordinator.Transfers;

public sealed record OutboundTransferInfo(
    Guid TransferId,
    Guid DeviceId,
    FileManifest Manifest,
    TransferState State,
    string? Error);

public sealed record OutboundChunk(byte[] Content, string Sha256Hex);

public sealed class OutboundTransferCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<Guid, OutboundTransfer> _transfers = new();

    public async Task<OutboundTransferInfo> EnqueueAsync(
        Guid deviceId,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("A device identifier is required.", nameof(deviceId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The source file does not exist.", fullPath);
        }

        string sha256Hex;
        await using (var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            sha256Hex = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        }

        file.Refresh();
        var manifest = new FileManifest(
            Guid.NewGuid(),
            file.Name,
            file.Length,
            file.LastWriteTimeUtc,
            ProtocolConstants.DefaultChunkSize,
            sha256Hex);
        manifest.Validate();

        var transfer = new OutboundTransfer(deviceId, fullPath, manifest);
        if (!_transfers.TryAdd(manifest.TransferId, transfer))
        {
            transfer.Gate.Dispose();
            throw new InvalidOperationException("Could not allocate a unique transfer identifier.");
        }

        return transfer.ToInfo();
    }

    public IReadOnlyList<OutboundTransferInfo> GetAvailable(Guid deviceId) =>
        _transfers.Values
            .Where(transfer => transfer.DeviceId == deviceId)
            .Where(transfer => transfer.StateMachine.State is
                TransferState.Queued or
                TransferState.Transferring or
                TransferState.Paused or
                TransferState.WaitingForConnection)
            .Select(transfer => transfer.ToInfo())
            .OrderBy(transfer => transfer.Manifest.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public OutboundTransferInfo? Get(Guid transferId, Guid deviceId) =>
        _transfers.TryGetValue(transferId, out var transfer) && transfer.DeviceId == deviceId
            ? transfer.ToInfo()
            : null;

    public async Task<OutboundChunk> ReadChunkAsync(
        Guid transferId,
        Guid deviceId,
        int chunkIndex,
        CancellationToken cancellationToken = default)
    {
        var transfer = GetOwnedTransfer(transferId, deviceId);
        await transfer.Gate.WaitAsync(cancellationToken);
        try
        {
            if (transfer.StateMachine.State == TransferState.Queued)
            {
                transfer.StateMachine.TransitionTo(TransferState.Transferring);
            }

            if (transfer.StateMachine.State != TransferState.Transferring)
            {
                throw new InvalidOperationException(
                    $"Chunks cannot be read while the transfer is {transfer.StateMachine.State}.");
            }

            var chunk = new ChunkPlan(transfer.Manifest).GetChunk(chunkIndex);
            var file = new FileInfo(transfer.SourcePath);
            if (!file.Exists ||
                file.Length != transfer.Manifest.Length ||
                file.LastWriteTimeUtc != transfer.Manifest.LastModifiedUtc.UtcDateTime)
            {
                transfer.Error = "The source file changed after it was queued.";
                transfer.StateMachine.TransitionTo(TransferState.Failed);
                throw new IOException(transfer.Error);
            }

            var content = new byte[chunk.Length];
            await using var stream = new FileStream(
                transfer.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            stream.Position = chunk.Offset;
            await stream.ReadExactlyAsync(content, cancellationToken);
            return new OutboundChunk(content, Convert.ToHexString(SHA256.HashData(content)));
        }
        catch (Exception exception)
        {
            transfer.Error ??= exception.Message;
            throw;
        }
        finally
        {
            transfer.Gate.Release();
        }
    }

    public async Task<bool> MarkCompletedAsync(
        Guid transferId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!_transfers.TryGetValue(transferId, out var transfer) || transfer.DeviceId != deviceId)
        {
            return false;
        }

        await transfer.Gate.WaitAsync(cancellationToken);
        try
        {
            if (transfer.StateMachine.State == TransferState.Completed)
            {
                return true;
            }

            if (transfer.StateMachine.State == TransferState.Queued && transfer.Manifest.ChunkCount == 0)
            {
                transfer.StateMachine.TransitionTo(TransferState.Transferring);
            }

            if (transfer.StateMachine.State != TransferState.Transferring)
            {
                return false;
            }

            transfer.StateMachine.TransitionTo(TransferState.Verifying);
            transfer.StateMachine.TransitionTo(TransferState.Completed);
            return true;
        }
        finally
        {
            transfer.Gate.Release();
        }
    }

    public async Task<bool> CancelAsync(
        Guid transferId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!_transfers.TryGetValue(transferId, out var transfer) || transfer.DeviceId != deviceId)
        {
            return false;
        }

        await transfer.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!transfer.StateMachine.CanTransitionTo(TransferState.Canceled))
            {
                return false;
            }

            transfer.StateMachine.TransitionTo(TransferState.Canceled);
            return true;
        }
        finally
        {
            transfer.Gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var transfer in _transfers.Values)
        {
            transfer.Gate.Dispose();
        }
    }

    private OutboundTransfer GetOwnedTransfer(Guid transferId, Guid deviceId)
    {
        if (!_transfers.TryGetValue(transferId, out var transfer) || transfer.DeviceId != deviceId)
        {
            throw new KeyNotFoundException("The transfer does not exist for this device.");
        }

        return transfer;
    }

    private sealed class OutboundTransfer(Guid deviceId, string sourcePath, FileManifest manifest)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public Guid DeviceId { get; } = deviceId;

        public string SourcePath { get; } = sourcePath;

        public FileManifest Manifest { get; } = manifest;

        public TransferStateMachine StateMachine { get; } = new(TransferState.Queued);

        public string? Error { get; set; }

        public OutboundTransferInfo ToInfo() =>
            new(Manifest.TransferId, DeviceId, Manifest, StateMachine.State, Error);
    }
}
