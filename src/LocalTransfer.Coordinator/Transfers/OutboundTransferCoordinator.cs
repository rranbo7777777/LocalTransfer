using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Coordinator.History;
using LocalTransfer.Core.Transfers;

namespace LocalTransfer.Coordinator.Transfers;

public sealed record OutboundTransferInfo(
    Guid TransferId,
    Guid DeviceId,
    FileManifest Manifest,
    TransferState State,
    string? Error,
    int ChunksServed = 0);

public sealed record OutboundChunk(byte[] Content, string Sha256Hex);

public sealed class OutboundTransferCoordinator : IDisposable
{
    // Terminal-state transfers are kept briefly so the peer can observe the final status,
    // then swept to bound dictionary growth in the long-running desktop service.
    private static readonly TimeSpan TerminalRetention = TransferTuning.TerminalRetention;

    private static readonly JsonSerializerOptions PersistOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, OutboundTransfer> _transfers = new();

    private readonly object _persistGate = new();
    private readonly string? _queueFilePath;
    private readonly TransferHistoryStore? _history;

    public OutboundTransferCoordinator(
        string? dataDirectory = null,
        TransferHistoryStore? history = null)
    {
        _history = history;
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return;
        }

        _queueFilePath = Path.Combine(dataDirectory, "outbound-queue.json");
        RestoreQueue();
    }

    public async Task<OutboundTransferInfo> EnqueueAsync(
        Guid deviceId,
        string sourcePath,
        IProgress<double>? hashProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("A device identifier is required.", nameof(deviceId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        SweepTerminalTransfers();
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
            if (hashProgress is null)
            {
                sha256Hex = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            }
            else
            {
                // Hash in 1 MiB blocks and report whole-percent progress so the desktop UI can
                // show movement on multi-gigabyte files instead of appearing frozen.
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                long totalRead = 0;
                var lastReported = -1;
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    totalRead += read;
                    var percent = file.Length == 0
                        ? 100
                        : (int)Math.Min(99, totalRead * 100 / file.Length);
                    if (percent != lastReported)
                    {
                        lastReported = percent;
                        hashProgress.Report(percent);
                    }
                }

                sha256Hex = Convert.ToHexString(hasher.GetHashAndReset());
                hashProgress.Report(100);
            }
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

        var transfer = CreateTransfer(deviceId, fullPath, manifest);
        if (!_transfers.TryAdd(manifest.TransferId, transfer))
        {
            transfer.Gate.Dispose();
            throw new InvalidOperationException("Could not allocate a unique transfer identifier.");
        }

        PersistQueue();
        return transfer.ToInfo();
    }

    public IReadOnlyList<OutboundTransferInfo> GetAvailable(Guid deviceId)
    {
        SweepTerminalTransfers();
        return _transfers.Values
            .Where(transfer => transfer.DeviceId == deviceId)
            .Where(transfer => transfer.StateMachine.State is
                TransferState.Queued or
                TransferState.Transferring or
                TransferState.Paused or
                TransferState.WaitingForConnection)
            .Select(transfer => transfer.ToInfo())
            .OrderBy(transfer => transfer.Manifest.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Snapshot of every outbound transfer regardless of device. The desktop UI needs a single
    /// list to render the "transfers" page; the HTTP surface only ever asks per device.
    /// </summary>
    public IReadOnlyList<OutboundTransferInfo> GetAll()
    {
        SweepTerminalTransfers();
        return _transfers.Values
            .Select(transfer => transfer.ToInfo())
            .OrderBy(info => info.Manifest.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public OutboundTransferInfo? Get(Guid transferId, Guid deviceId)
    {
        SweepTerminalTransfers();
        return _transfers.TryGetValue(transferId, out var transfer) && transfer.DeviceId == deviceId
            ? transfer.ToInfo()
            : null;
    }

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
                Math.Abs((file.LastWriteTimeUtc - transfer.Manifest.LastModifiedUtc.UtcDateTime).TotalSeconds) > 2)
            {
                transfer.Error = "The source file changed after it was queued.";
                transfer.StateMachine.TransitionTo(TransferState.Failed);
                PersistQueue();
                throw new IOException(transfer.Error);
            }

            // Track how much of the file the peer has pulled. The desktop "transfers" page turns
            // this into a percentage; it is an upper bound on real progress (the peer may not have
            // flushed every chunk it requested) but moves monotonically and is far better than a
            // bare "transferring" label on a multi-gigabyte file.
            transfer.RequestedChunks.Add(chunkIndex);

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
            // Error strings are served to the paired peer; file I/O exception messages
            // contain local filesystem paths and must be replaced.
            transfer.Error ??= exception is IOException or UnauthorizedAccessException
                ? "A local file error occurred."
                : exception.Message;
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
            PersistQueue();
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
            PersistQueue();
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

    private OutboundTransfer CreateTransfer(Guid deviceId, string sourcePath, FileManifest manifest)
    {
        var transfer = new OutboundTransfer(deviceId, sourcePath, manifest);

        // Same approach as the inbound side: record on the terminal transition so no ending path
        // (completed, cancelled, failed) can be added later without also being recorded.
        transfer.StateMachine.StateChanged += (_, state) =>
        {
            if (ToOutcome(state) is { } outcome)
            {
                RecordTerminal(transfer, outcome);
            }
        };

        return transfer;
    }

    private static TransferOutcome? ToOutcome(TransferState state) => state switch
    {
        TransferState.Completed => TransferOutcome.Completed,
        TransferState.Failed => TransferOutcome.Failed,
        TransferState.Rejected => TransferOutcome.Rejected,
        TransferState.Canceled => TransferOutcome.Canceled,
        _ => null
    };

    private void RecordTerminal(OutboundTransfer transfer, TransferOutcome outcome)
    {
        if (_history is null || Interlocked.Exchange(ref transfer.HistoryRecorded, 1) != 0)
        {
            return;
        }

        _history.Append(new TransferHistoryEntry(
            transfer.Manifest.TransferId,
            transfer.DeviceId,
            TransferDirection.Outbound,
            transfer.Manifest.FileName,
            transfer.Manifest.Length,
            DateTimeOffset.UtcNow,
            outcome,
            transfer.Error));
    }

    private OutboundTransfer GetOwnedTransfer(Guid transferId, Guid deviceId)
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
        var removed = false;
        foreach (var pair in _transfers)
        {
            var transfer = pair.Value;
            var state = transfer.StateMachine.State;
            if (state is not (TransferState.Completed or TransferState.Failed or
                TransferState.Canceled or TransferState.Rejected))
            {
                continue;
            }

            transfer.TerminalAtUtc ??= now;
            if (now - transfer.TerminalAtUtc.Value >= TerminalRetention)
            {
                _transfers.TryRemove(pair.Key, out _);
                removed = true;
            }
        }

        if (removed)
        {
            PersistQueue();
        }
    }

    private sealed record PersistedOutboundTransfer(
        Guid TransferId,
        Guid DeviceId,
        string SourcePath,
        FileManifest Manifest);

    /// <summary>
    /// Rewrites the on-disk queue file from the in-memory state. Only non-terminal transfers
    /// are persisted, so a completed/canceled/failed transfer disappears from the file the
    /// moment it reaches its terminal state. Persistence is best-effort: a failed write must
    /// never abort the transfer operation that triggered it.
    /// </summary>
    private void PersistQueue()
    {
        if (_queueFilePath is null)
        {
            return;
        }

        var entries = _transfers.Values
            .Where(transfer => transfer.StateMachine.State is not
                (TransferState.Completed or TransferState.Failed or
                TransferState.Canceled or TransferState.Rejected))
            .Select(transfer => new PersistedOutboundTransfer(
                transfer.Manifest.TransferId,
                transfer.DeviceId,
                transfer.SourcePath,
                transfer.Manifest))
            .ToArray();

        try
        {
            lock (_persistGate)
            {
                File.WriteAllText(
                    _queueFilePath,
                    JsonSerializer.Serialize(entries, PersistOptions));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best-effort by design; the queue simply falls back to memory-only for this run.
        }
    }

    /// <summary>
    /// Re-queues the transfers persisted by <see cref="PersistQueue"/>. An entry only survives
    /// when its source file still exists with the exact length and (within tolerance) the
    /// timestamp recorded in the manifest — the same freshness rule <see cref="ReadChunkAsync"/>
    /// enforces, so a stale entry fails here instead of failing mid-transfer later. Entries that
    /// reference missing or changed files are dropped and purged from the file.
    /// </summary>
    private void RestoreQueue()
    {
        List<PersistedOutboundTransfer>? entries;
        try
        {
            if (!File.Exists(_queueFilePath))
            {
                return;
            }

            entries = JsonSerializer.Deserialize<List<PersistedOutboundTransfer>>(
                File.ReadAllText(_queueFilePath), PersistOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // A corrupt queue file must never keep the desktop service from starting.
            return;
        }

        if (entries is null || entries.Count == 0)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.TransferId == Guid.Empty ||
                entry.DeviceId == Guid.Empty ||
                string.IsNullOrWhiteSpace(entry.SourcePath) ||
                entry.Manifest is null)
            {
                continue;
            }

            try
            {
                entry.Manifest.Validate();
            }
            catch (ArgumentException)
            {
                continue;
            }

            var file = new FileInfo(entry.SourcePath);
            if (!file.Exists ||
                file.Length != entry.Manifest.Length ||
                Math.Abs((file.LastWriteTimeUtc - entry.Manifest.LastModifiedUtc.UtcDateTime).TotalSeconds) > 2)
            {
                continue;
            }

            var transfer = CreateTransfer(entry.DeviceId, entry.SourcePath, entry.Manifest);
            if (_transfers.TryAdd(entry.TransferId, transfer))
            {
                transfer.Restored = true;
            }
            else
            {
                transfer.Gate.Dispose();
            }
        }

        PersistQueue();
    }

    private sealed class OutboundTransfer(Guid deviceId, string sourcePath, FileManifest manifest)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Guards against recording the same transfer in the history twice.</summary>
        public int HistoryRecorded;

        public Guid DeviceId { get; } = deviceId;

        public string SourcePath { get; } = sourcePath;

        public FileManifest Manifest { get; } = manifest;

        public TransferStateMachine StateMachine { get; } = new(TransferState.Queued);

        public string? Error { get; set; }

        public DateTimeOffset? TerminalAtUtc { get; set; }

        /// <summary>Distinct chunk indexes the peer has requested while holding the gate.</summary>
        public HashSet<int> RequestedChunks { get; } = new();

        /// <summary>True when this instance came from the persisted queue, not a live enqueue.</summary>
        public bool Restored { get; set; }

        public OutboundTransferInfo ToInfo() =>
            new(Manifest.TransferId, DeviceId, Manifest, StateMachine.State, Error, RequestedChunks.Count);
    }
}
