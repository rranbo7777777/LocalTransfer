using System.Collections.Concurrent;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Core.Transfers;
using LocalTransfer.Infrastructure.Transfers;

namespace LocalTransfer.Coordinator.Transfers;

public sealed record InboundTransferInfo(
    Guid TransferId,
    Guid DeviceId,
    FileManifest Manifest,
    TransferState State,
    IReadOnlyList<int> MissingChunks,
    string? FinalPath,
    string? Error);

public sealed class InboundTransferCoordinator : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, InboundTransfer> _transfers = new();
    private readonly ResumableFileReceiver _receiver = new();
    private readonly string _receiveDirectory;

    public InboundTransferCoordinator(string receiveDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiveDirectory);
        _receiveDirectory = Path.GetFullPath(receiveDirectory);
        Directory.CreateDirectory(_receiveDirectory);
    }

    public event EventHandler<InboundTransferInfo>? TransferOffered;

    public InboundTransferInfo SubmitOffer(Guid deviceId, FileManifest manifest)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("A device identifier is required.", nameof(deviceId));
        }

        manifest.Validate();
        var transfer = new InboundTransfer(deviceId, manifest);
        if (!_transfers.TryAdd(manifest.TransferId, transfer))
        {
            var existing = _transfers[manifest.TransferId];
            if (existing.DeviceId != deviceId || existing.Manifest != manifest)
            {
                throw new InvalidOperationException("The transfer identifier is already in use.");
            }

            return existing.ToInfo([]);
        }

        var info = transfer.ToInfo([]);
        TransferOffered?.Invoke(this, info);
        return info;
    }

    public IReadOnlyList<InboundTransferInfo> GetPendingOffers() =>
        _transfers.Values
            .Where(transfer => transfer.StateMachine.State == TransferState.WaitingForApproval)
            .Select(transfer => transfer.ToInfo([]))
            .OrderBy(transfer => transfer.Manifest.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public async Task<bool> ApproveAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        if (!_transfers.TryGetValue(transferId, out var transfer))
        {
            return false;
        }

        await transfer.Gate.WaitAsync(cancellationToken);
        try
        {
            if (transfer.StateMachine.State != TransferState.WaitingForApproval)
            {
                return false;
            }

            transfer.Session = await _receiver.OpenAsync(transfer.Manifest, _receiveDirectory, cancellationToken);
            transfer.StateMachine.TransitionTo(TransferState.Queued);
            return true;
        }
        catch (Exception exception)
        {
            transfer.Error = exception.Message;
            throw;
        }
        finally
        {
            transfer.Gate.Release();
        }
    }

    public async Task<bool> RejectAsync(
        Guid transferId,
        CancellationToken cancellationToken = default)
    {
        if (!_transfers.TryGetValue(transferId, out var transfer))
        {
            return false;
        }

        await transfer.Gate.WaitAsync(cancellationToken);
        try
        {
            if (transfer.StateMachine.State != TransferState.WaitingForApproval)
            {
                return false;
            }

            transfer.StateMachine.TransitionTo(TransferState.Rejected);
            return true;
        }
        finally
        {
            transfer.Gate.Release();
        }
    }

    public async Task<InboundTransferInfo?> GetAsync(
        Guid transferId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!_transfers.TryGetValue(transferId, out var transfer) || transfer.DeviceId != deviceId)
        {
            return null;
        }

        var missing = transfer.Session is null
            ? Array.Empty<int>()
            : await _receiver.GetMissingChunksAsync(transfer.Session, cancellationToken);
        return transfer.ToInfo(missing);
    }

    public async Task WriteChunkAsync(
        Guid transferId,
        Guid deviceId,
        int chunkIndex,
        Stream content,
        string chunkSha256Hex,
        CancellationToken cancellationToken = default)
    {
        var transfer = GetOwnedTransfer(transferId, deviceId);
        await transfer.Gate.WaitAsync(cancellationToken);
        try
        {
            if (transfer.Session is null)
            {
                throw new InvalidOperationException("The transfer has not been approved.");
            }

            if (transfer.StateMachine.State == TransferState.Queued)
            {
                transfer.StateMachine.TransitionTo(TransferState.Transferring);
            }

            if (transfer.StateMachine.State != TransferState.Transferring)
            {
                throw new InvalidOperationException($"Chunks cannot be written while the transfer is {transfer.StateMachine.State}.");
            }

            await _receiver.WriteChunkAsync(
                transfer.Session,
                chunkIndex,
                content,
                chunkSha256Hex,
                cancellationToken);
        }
        catch (Exception exception)
        {
            transfer.Error = exception.Message;
            throw;
        }
        finally
        {
            transfer.Gate.Release();
        }
    }

    public async Task<string> CompleteAsync(
        Guid transferId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var transfer = GetOwnedTransfer(transferId, deviceId);
        await transfer.Gate.WaitAsync(cancellationToken);
        try
        {
            if (transfer.StateMachine.State == TransferState.Completed && transfer.FinalPath is not null)
            {
                return transfer.FinalPath;
            }

            if (transfer.Session is null)
            {
                throw new InvalidOperationException("The transfer has not been approved.");
            }

            if (transfer.StateMachine.State == TransferState.Queued && transfer.Manifest.ChunkCount == 0)
            {
                transfer.StateMachine.TransitionTo(TransferState.Transferring);
            }

            if (transfer.StateMachine.State != TransferState.Transferring)
            {
                throw new InvalidOperationException("The transfer is not ready for verification.");
            }

            transfer.StateMachine.TransitionTo(TransferState.Verifying);
            try
            {
                transfer.FinalPath = await _receiver.CompleteAsync(transfer.Session, cancellationToken);
                transfer.StateMachine.TransitionTo(TransferState.Completed);
                return transfer.FinalPath;
            }
            catch (Exception exception)
            {
                transfer.Error = exception.Message;
                transfer.StateMachine.TransitionTo(TransferState.Failed);
                throw;
            }
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

            if (transfer.Session is not null)
            {
                await _receiver.CancelAsync(transfer.Session, cancellationToken);
            }

            transfer.StateMachine.TransitionTo(TransferState.Canceled);
            return true;
        }
        finally
        {
            transfer.Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var transfer in _transfers.Values)
        {
            if (transfer.Session is not null)
            {
                await transfer.Session.DisposeAsync();
            }

            transfer.Gate.Dispose();
        }
    }

    private InboundTransfer GetOwnedTransfer(Guid transferId, Guid deviceId)
    {
        if (!_transfers.TryGetValue(transferId, out var transfer) || transfer.DeviceId != deviceId)
        {
            throw new KeyNotFoundException("The transfer does not exist for this device.");
        }

        return transfer;
    }

    private sealed class InboundTransfer(Guid deviceId, FileManifest manifest)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public Guid DeviceId { get; } = deviceId;

        public FileManifest Manifest { get; } = manifest;

        public TransferStateMachine StateMachine { get; } = new();

        public ReceiveSession? Session { get; set; }

        public string? FinalPath { get; set; }

        public string? Error { get; set; }

        public InboundTransferInfo ToInfo(IReadOnlyList<int> missingChunks) =>
            new(
                Manifest.TransferId,
                DeviceId,
                Manifest,
                StateMachine.State,
                missingChunks,
                FinalPath,
                Error);
    }
}
