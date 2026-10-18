using System.Collections.Concurrent;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Coordinator.History;
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
    DateTimeOffset ExpiresAtUtc = default,
    int ReceivedChunks = 0);

/// <summary>
/// One confirmation prompt covering a whole batch, instead of one prompt per file. The desktop
/// shows the list and answers once.
/// </summary>
public sealed record InboundBatchInfo(
    Guid BatchId,
    Guid DeviceId,
    IReadOnlyList<InboundTransferInfo> Files,
    DateTimeOffset ExpiresAtUtc);

public sealed class InboundTransferCoordinator : IAsyncDisposable
{
    private const int MaxPendingOffersPerDevice = 10;

    /// <summary>
    /// How long an unanswered "may I send you this file?" prompt stays open. It has to outlast
    /// the client's own approval wait (<see cref="TransferTuning.ClientApprovalWait"/>) so a phone
    /// that is still polling never sees its offer disappear underneath it.
    /// </summary>
    public static readonly TimeSpan DefaultOfferLifetime = TransferTuning.OfferLifetime;

    // Terminal-state transfers are kept briefly so the peer can observe the final status,
    // then swept to bound dictionary growth in the long-running desktop service.
    private static readonly TimeSpan TerminalRetention = TransferTuning.TerminalRetention;

    private readonly ConcurrentDictionary<Guid, InboundTransfer> _transfers = new();

    /// <summary>Batches whose confirmation prompt is still open, keyed by batch id.</summary>
    private readonly ConcurrentDictionary<Guid, PendingBatch> _batches = new();
    private readonly ResumableFileReceiver _receiver = new();
    private readonly string _receiveDirectory;
    private readonly TimeSpan _offerLifetime;
    private readonly TransferHistoryStore? _history;

    public InboundTransferCoordinator(
        string receiveDirectory,
        TimeSpan? offerLifetime = null,
        TransferHistoryStore? history = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiveDirectory);
        _receiveDirectory = Path.GetFullPath(receiveDirectory);
        _offerLifetime = offerLifetime ?? DefaultOfferLifetime;
        _history = history;
        if (_offerLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offerLifetime),
                "The offer lifetime must be positive.");
        }

        Directory.CreateDirectory(_receiveDirectory);
    }

    public event EventHandler<InboundTransferInfo>? TransferOffered;

    /// <summary>
    /// Raised once per batch instead of once per file. Subscribers answer with
    /// <see cref="ApproveBatchAsync"/> or <see cref="RejectBatchAsync"/>.
    /// </summary>
    public event EventHandler<InboundBatchInfo>? BatchOffered;

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
        var (transfer, created) = CreateTransfer(deviceId, manifest);
        if (!created)
        {
            return transfer.ToInfo([]);
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
        CoordinatorLog.Write(
            $"offer {manifest.TransferId:N} from device {deviceId:N}: " +
            $"\"{manifest.FileName}\" {manifest.Length} bytes in {manifest.ChunkCount} chunk(s)");
        TransferOffered?.Invoke(this, info);
        return info;
    }

    /// <summary>
    /// Registers every file of a batch, raises a single confirmation prompt, and returns only once
    /// the desktop has answered.
    /// <para>
    /// This is what replaces N prompts with one. A hundred files used to mean a hundred modal
    /// dialogs, each with its own ten-minute window, which made sending a batch effectively
    /// unusable no matter how reliable the transfer itself was.
    /// </para>
    /// <para>
    /// The per-device pending-offer cap does not apply here: the cap exists to stop prompts piling
    /// up, and a batch is exactly one prompt.
    /// </para>
    /// </summary>
    public async Task<BatchSubmissionResult> SubmitBatchAsync(
        Guid deviceId,
        Guid batchId,
        IReadOnlyList<FileManifest> manifests,
        CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("A device identifier is required.", nameof(deviceId));
        }

        if (batchId == Guid.Empty)
        {
            throw new ArgumentException("A batch identifier is required.", nameof(batchId));
        }

        ArgumentNullException.ThrowIfNull(manifests);
        if (manifests.Count == 0)
        {
            throw new ArgumentException("A batch needs at least one file.", nameof(manifests));
        }

        if (manifests.Count > BatchLimits.MaxFiles)
        {
            throw new ArgumentException(
                $"A batch may contain at most {BatchLimits.MaxFiles} files.",
                nameof(manifests));
        }

        foreach (var manifest in manifests)
        {
            manifest.Validate();
        }

        SweepTerminalTransfers();
        SweepExpiredOffers();

        var transfers = new List<InboundTransfer>(manifests.Count);
        try
        {
            foreach (var manifest in manifests)
            {
                transfers.Add(CreateTransfer(deviceId, manifest).Transfer);
            }
        }
        catch
        {
            // Half a batch is worse than none: roll back so a retry starts clean.
            RollBack(transfers);
            throw;
        }

        var pending = new PendingBatch(
            deviceId,
            transfers,
            new TaskCompletionSource<HashSet<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_batches.TryAdd(batchId, pending))
        {
            RollBack(transfers);
            throw new InvalidOperationException("The batch identifier is already in use.");
        }

        CoordinatorLog.Write(
            $"batch {batchId:N} from device {deviceId:N}: {manifests.Count} file(s), " +
            $"{manifests.Sum(manifest => manifest.Length)} bytes");

        BatchOffered?.Invoke(
            this,
            new InboundBatchInfo(
                batchId,
                deviceId,
                transfers.Select(transfer => transfer.ToInfo([])).ToArray(),
                transfers.Min(transfer => transfer.ExpiresAtUtc)));

        HashSet<Guid> accepted;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_offerLifetime);
        try
        {
            accepted = await pending.Completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            // Nobody answered in time, or the phone hung up. Close the batch so its files do not
            // sit in WaitingForApproval and keep their slots.
            accepted = [];
            await RejectBatchAsync(batchId, deviceId);
        }

        var acceptedIds = transfers
            .Where(transfer => accepted.Contains(transfer.Manifest.TransferId))
            .Select(transfer => transfer.Manifest.TransferId)
            .ToArray();
        var rejectedIds = transfers
            .Where(transfer => !accepted.Contains(transfer.Manifest.TransferId))
            .Select(transfer => transfer.Manifest.TransferId)
            .ToArray();

        return new BatchSubmissionResult(batchId, acceptedIds, rejectedIds);
    }

    /// <summary>
    /// Answers a batch: the listed transfers are approved (which opens their receive session) and
    /// the rest are rejected. Idempotent - a second answer for the same batch returns false.
    /// </summary>
    public async Task<bool> ApproveBatchAsync(
        Guid batchId,
        Guid deviceId,
        IReadOnlySet<Guid> acceptedTransferIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acceptedTransferIds);
        if (!_batches.TryGetValue(batchId, out var batch) || batch.DeviceId != deviceId)
        {
            return false;
        }

        await batch.AnswerAsync(async transfer =>
        {
            if (acceptedTransferIds.Contains(transfer.Manifest.TransferId))
            {
                await ApproveAsync(transfer.Manifest.TransferId, cancellationToken);
            }
            else
            {
                await RejectAsync(transfer.Manifest.TransferId, cancellationToken);
            }
        });

        _batches.TryRemove(batchId, out _);
        batch.Completion.TrySetResult(new HashSet<Guid>(acceptedTransferIds));
        CoordinatorLog.Write(
            $"batch {batchId:N} answered: {acceptedTransferIds.Count} of {batch.Transfers.Count} accepted");
        return true;
    }

    /// <summary>Rejects every file of a batch in one go.</summary>
    public async Task<bool> RejectBatchAsync(
        Guid batchId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!_batches.TryGetValue(batchId, out var batch) || batch.DeviceId != deviceId)
        {
            return false;
        }

        await batch.AnswerAsync(transfer => RejectAsync(transfer.Manifest.TransferId, cancellationToken));
        _batches.TryRemove(batchId, out _);
        batch.Completion.TrySetResult([]);
        CoordinatorLog.Write($"batch {batchId:N} rejected");
        return true;
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
    /// a summary row; the synchronous <see cref="ReceiveSession.CompletedChunkCount"/> supplies
    /// receive progress instead.
    /// </summary>
    public IReadOnlyList<InboundTransferInfo> GetAll()
    {
        SweepTerminalTransfers();
        SweepExpiredOffers();
        return _transfers.Values
            .Select(transfer => transfer.ToInfo(
                [],
                transfer.Session?.CompletedChunkCount ?? 0))
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
                CoordinatorLog.Write(
                    $"approve {transferId:N} refused: the offer expired at {transfer.ExpiresAtUtc:u}");
                return false;
            }

            transfer.Session = await _receiver.OpenAsync(transfer.Manifest, _receiveDirectory, cancellationToken);
            transfer.StateMachine.TransitionTo(TransferState.Queued);
            CoordinatorLog.Write($"approve {transferId:N}: queued for receiving");
            return true;
        }
        catch (Exception exception)
        {
            transfer.Error = DescribeFailure(exception);
            CoordinatorLog.Write(
                $"approve {transferId:N} failed: {exception.GetType().Name}: {exception.Message}");
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
            CoordinatorLog.Write($"reject {transferId:N}: refused on the desktop");
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
        return transfer.ToInfo(
            missing,
            transfer.Session?.CompletedChunkCount ?? 0);
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
            CoordinatorLog.Write(
                $"chunk {chunkIndex} of {transferId:N} failed: {exception.GetType().Name}: {exception.Message}");
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
                CoordinatorLog.Write(
                    $"completed {transferId:N}: \"{transfer.Manifest.FileName}\" -> " +
                    $"\"{Path.GetFileName(transfer.FinalPath)}\"");
                return transfer.FinalPath;
            }
            catch (Exception exception)
            {
                transfer.Error = DescribeFailure(exception);
                transfer.StateMachine.TransitionTo(TransferState.Failed);
                CoordinatorLog.Write(
                    $"complete {transferId:N} failed: {exception.GetType().Name}: {exception.Message}");
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

    private (InboundTransfer Transfer, bool Created) CreateTransfer(Guid deviceId, FileManifest manifest)
    {
        var transfer = new InboundTransfer(deviceId, manifest, DateTimeOffset.UtcNow.Add(_offerLifetime));

        // Recording on the terminal transition rather than at each call site keeps every way of
        // ending a transfer (completed, rejected, expired, cancelled) in the history, with no path
        // able to forget it.
        transfer.StateMachine.StateChanged += (_, state) =>
        {
            if (ToOutcome(state) is { } outcome)
            {
                RecordTerminal(transfer, outcome);
            }
        };

        if (_transfers.TryAdd(manifest.TransferId, transfer))
        {
            return (transfer, true);
        }

        var existing = _transfers[manifest.TransferId];
        if (existing.DeviceId != deviceId || existing.Manifest != manifest)
        {
            throw new InvalidOperationException("The transfer identifier is already in use.");
        }

        return (existing, false);
    }

    /// <summary>Drops transfers registered by a batch that could not be completed.</summary>
    private void RollBack(IEnumerable<InboundTransfer> transfers)
    {
        foreach (var transfer in transfers)
        {
            _transfers.TryRemove(transfer.Manifest.TransferId, out _);
            transfer.Gate.Dispose();
        }
    }

    private static TransferOutcome? ToOutcome(TransferState state) => state switch
    {
        TransferState.Completed => TransferOutcome.Completed,
        TransferState.Failed => TransferOutcome.Failed,
        TransferState.Rejected => TransferOutcome.Rejected,
        TransferState.Canceled => TransferOutcome.Canceled,
        _ => null
    };

    private void RecordTerminal(InboundTransfer transfer, TransferOutcome outcome)
    {
        if (_history is null || Interlocked.Exchange(ref transfer.HistoryRecorded, 1) != 0)
        {
            return;
        }

        _history.Append(new TransferHistoryEntry(
            transfer.Manifest.TransferId,
            transfer.DeviceId,
            TransferDirection.Inbound,
            transfer.Manifest.FileName,
            transfer.Manifest.Length,
            DateTimeOffset.UtcNow,
            outcome,
            transfer.Error));
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
                    CoordinatorLog.Write(
                        $"offer {transfer.Manifest.TransferId:N} expired unanswered; " +
                        "its per-device slot is released again");
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

    /// <summary>
    /// A batch whose prompt is still open, plus the single-use latch that keeps a late timeout and
    /// a user click from both driving the same transfers.
    /// </summary>
    private sealed class PendingBatch(
        Guid deviceId,
        IReadOnlyList<InboundTransfer> transfers,
        TaskCompletionSource<HashSet<Guid>> completion)
    {
        private int _answered;

        public Guid DeviceId { get; } = deviceId;

        public IReadOnlyList<InboundTransfer> Transfers { get; } = transfers;

        public TaskCompletionSource<HashSet<Guid>> Completion { get; } = completion;

        public async Task AnswerAsync(Func<InboundTransfer, Task> answer)
        {
            if (Interlocked.Exchange(ref _answered, 1) != 0)
            {
                return;
            }

            foreach (var transfer in Transfers)
            {
                await answer(transfer);
            }
        }
    }

    private sealed class InboundTransfer(Guid deviceId, FileManifest manifest, DateTimeOffset expiresAtUtc)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Guards against recording the same transfer in the history twice.</summary>
        public int HistoryRecorded;

        public Guid DeviceId { get; } = deviceId;

        public FileManifest Manifest { get; } = manifest;

        /// <summary>When the unanswered approval prompt closes itself.</summary>
        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;

        public TransferStateMachine StateMachine { get; } = new();

        public ReceiveSession? Session { get; set; }

        public string? FinalPath { get; set; }

        public string? Error { get; set; }

        public DateTimeOffset? TerminalAtUtc { get; set; }

        public InboundTransferInfo ToInfo(IReadOnlyList<int> missingChunks, int receivedChunks = 0) =>
            new(
                Manifest.TransferId,
                DeviceId,
                Manifest,
                StateMachine.State,
                missingChunks,
                FinalPath,
                Error,
                ExpiresAtUtc,
                receivedChunks);
    }
}
