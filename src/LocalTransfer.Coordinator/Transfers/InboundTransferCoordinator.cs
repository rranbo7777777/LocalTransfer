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
    string? Error,
    DateTimeOffset ExpiresAtUtc = default);

public sealed class InboundTransferCoordinator : IAsyncDisposable
{
    private const int MaxPendingOffersPerDevice = 10;

    /// <summary>
    /// How long an unanswered "may I send you this file?" prompt stays open. It has to outlast
    /// the client's own approval wait (ten minutes, see <c>LocalTransferClient.UploadAsync</c>)
    /// so a phone that is still polling never sees its offer disappear underneath it.
    /// </summary>
    public static readonly TimeSpan DefaultOfferLifetime = TimeSpan.FromMinutes(11);

    // Terminal-state transfers are kept briefly so the peer can observe the final status,
    // then swept to bound dictionary growth in the long-running desktop service.
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, InboundTransfer> _transfers = new();
    private readonly ResumableFileReceiver _receiver = new();
    private readonly string _receiveDirectory;
    private readonly TimeSpan _offerLifetime;

    public InboundTransferCoordinator(string receiveDirectory, TimeSpan? offerLifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiveDirectory);
        _receiveDirectory = Path.GetFullPath(receiveDirectory);
        _offerLifetime = offerLifetime ?? DefaultOfferLifetime;
        if (_offerLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offerLifetime),
                "The offer lifetime must be positive.");
        }

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
        SweepTerminalTransfers();
        // Expiring first is what keeps the per-device cap below meaningful: an unanswered prompt
        // gives its slot back instead of counting against the phone forever.
        SweepExpiredOffers();
        var transfer = new InboundTransfer(deviceId, manifest, DateTimeOffset.UtcNow.Add(_offerLifetime));
        if (!_transfers.TryAdd(manifest.TransferId, transfer))
        {
            var existing = _transfers[manifest.TransferId];
            if (existing.DeviceId != deviceId || existing.Manifest != manifest)
            {
                throw new InvalidOperationException("The transfer identifier is already in use.");
            }

            return existing.ToInfo([]);
        }

        // The cap applies to new offers only; a retried submission of an already-registered
        // offer must stay idempotent regardless of how many other offers are pending.
        var pendingOffers = _transfers.Values.Count(transfer =>
            transfer.DeviceId == deviceId &&
            transfer.StateMachine.State == TransferState.WaitingForApproval);
        if (pendingOffers > MaxPendingOffersPerDevice)
        {
            _transfers.TryRemove(manifest.TransferId, out _);
            transfer.Gate.Dispose();
            throw new InvalidOperationException(
                $"The device already has {pendingOffers - 1} transfers awaiting approval.");
        }

        var info = transfer.ToInfo([]);
        TransferOffered?.Invoke(this, info);
        return info;
    }

    public IReadOnlyList<InboundTransferInfo> GetPendingOffers()
    {
        SweepExpiredOffers();
        return _transfers.Values
            .Where(transfer => transfer.StateMachine.State == TransferState.WaitingForApproval)
            .Select(transfer => transfer.ToInfo([]))
            .OrderBy(transfer => transfer.Manifest.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Snapshot of every inbound transfer regardless of device, for the desktop UI. The missing
    /// chunk list is omitted because computing it needs the async receiver and the UI only needs
    /// a summary row.
    /// </summary>
    public IReadOnlyList<InboundTransferInfo> GetAll()
    {
        SweepTerminalTransfers();
        SweepExpiredOffers();
        return _transfers.Values
            .Select(transfer => transfer.ToInfo([]))
            .OrderBy(info => info.Manifest.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

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

            // Checked here as well as in the sweep so a late "yes" can never be accepted just
            // because no other call happened to run the sweep first. The same mistake in the
            // pairing flow handed the phone a credential it had already stopped waiting for.
            if (DateTimeOffset.UtcNow > transfer.ExpiresAtUtc)
            {
                transfer.StateMachine.TransitionTo(TransferState.Rejected);
                return false;
            }

            transfer.Session = await _receiver.OpenAsync(transfer.Manifest, _receiveDirectory, cancellationToken);
            transfer.StateMachine.TransitionTo(TransferState.Queued);
            return true;
        }
        catch (Exception exception)
        {
            transfer.Error = DescribeFailure(exception);
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
        SweepTerminalTransfers();
        SweepExpiredOffers();
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
            transfer.Error = DescribeFailure(exception);
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
                transfer.Error = DescribeFailure(exception);
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

    private void SweepTerminalTransfers()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _transfers)
        {
            var transfer = pair.Value;
            var state = transfer.StateMachine.State;
            if (state is not (TransferState.Completed or TransferState.Failed or
                TransferState.Rejected or TransferState.Canceled))
            {
                continue;
            }

            transfer.TerminalAtUtc ??= now;
            if (now - transfer.TerminalAtUtc.Value >= TerminalRetention)
            {
                _transfers.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>
    /// Closes offers whose approval prompt was never answered.
    /// <para>
    /// Without this an ignored prompt left the transfer in <see cref="TransferState.WaitingForApproval"/>
    /// forever. Two things then went wrong: the phone kept polling for approval until its own
    /// timeout instead of being told "no", and every ignored prompt permanently consumed one of
    /// the ten per-device offer slots — after ten of them that phone could not send anything at
    /// all until the desktop was restarted.
    /// </para>
    /// </summary>
    private void SweepExpiredOffers()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var transfer in _transfers.Values)
        {
            if (now <= transfer.ExpiresAtUtc)
            {
                continue;
            }

            if (transfer.StateMachine.State != TransferState.WaitingForApproval)
            {
                continue;
            }

            // Never race an approval that is already in flight: if the gate is held, that
            // operation is about to change the state anyway and the next sweep will see it.
            if (!transfer.Gate.Wait(TimeSpan.Zero))
            {
                continue;
            }

            try
            {
                if (transfer.StateMachine.State == TransferState.WaitingForApproval)
                {
                    transfer.StateMachine.TransitionTo(TransferState.Rejected);
                }
            }
            finally
            {
                transfer.Gate.Release();
            }
        }
    }

    // Error strings are serialized into status responses served to the paired peer; file I/O
    // exception messages contain local filesystem paths and must be replaced.
    private static string DescribeFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException
            ? "A local file error occurred."
            : exception.Message;

    private sealed class InboundTransfer(Guid deviceId, FileManifest manifest, DateTimeOffset expiresAtUtc)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public Guid DeviceId { get; } = deviceId;

        public FileManifest Manifest { get; } = manifest;

        /// <summary>When the unanswered approval prompt closes itself.</summary>
        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;

        public TransferStateMachine StateMachine { get; } = new();

        public ReceiveSession? Session { get; set; }

        public string? FinalPath { get; set; }

        public string? Error { get; set; }

        public DateTimeOffset? TerminalAtUtc { get; set; }

        public InboundTransferInfo ToInfo(IReadOnlyList<int> missingChunks) =>
            new(
                Manifest.TransferId,
                DeviceId,
                Manifest,
                StateMachine.State,
                missingChunks,
                FinalPath,
                Error,
                ExpiresAtUtc);
    }
}
