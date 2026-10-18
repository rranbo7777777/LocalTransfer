using System.Buffers;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Core.Transfers;
using LocalTransfer.Infrastructure.Transfers;

namespace LocalTransfer.Client;

public sealed class LocalTransferClient : IDisposable
{
    private const int BufferSize = 128 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly CoordinatorConnection _connection;
    private readonly HttpClient _client;
    private readonly HttpClient _longRunningClient;
    private readonly ResumableFileReceiver _receiver = new();
    private readonly Action<string>? _diagnosticSink;

    public LocalTransferClient(CoordinatorConnection connection, Action<string>? diagnosticSink = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        // A sink rather than a member on the connection record: the record is persisted to secure
        // storage, and a delegate cannot survive that round trip.
        _diagnosticSink = diagnosticSink;
        _client = CreateClient(connection);

        // Batch approval blocks until a human answers, which can take minutes: the regular
        // two-minute per-request budget would abort it while the desktop is still deciding.
        _longRunningClient = CreateClient(connection, TransferTuning.BatchApprovalTimeout);
    }

    private static HttpClient CreateClient(CoordinatorConnection connection, TimeSpan? timeout = null)
    {
        var client = PinnedHttpClientFactory.Create(
            connection.Endpoint,
            connection.CertificateSha256,
            timeout);
        client.DefaultRequestHeaders.Add("X-LocalTransfer-Device", connection.DeviceId.ToString());
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", connection.Credential);
        return client;
    }

    public async Task<Guid> UploadAsync(
        string fileName,
        Func<CancellationToken, Task<Stream>> openReadAsync,
        DateTimeOffset? lastModifiedUtc = null,
        IProgress<TransferProgress>? progress = null,
        TimeSpan? approvalTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(openReadAsync);

        var hashAndLength = await HashAndMeasureAsync(openReadAsync, cancellationToken);
        var manifest = new FileManifest(
            Guid.NewGuid(),
            fileName,
            hashAndLength.Length,
            lastModifiedUtc ?? DateTimeOffset.UtcNow,
            ProtocolConstants.DefaultChunkSize,
            hashAndLength.Sha256Hex);
        manifest.Validate();

        using (var offerResponse = await SendWithRetryAsync(
                   () => new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
                   {
                       Content = JsonContent.Create(manifest, options: SerializerOptions)
                   },
                   cancellationToken))
        {
            offerResponse.EnsureSuccessStatusCode();
        }

        await WaitForApprovalAsync(
            manifest.TransferId,
            approvalTimeout ?? TransferTuning.ClientApprovalWait,
            cancellationToken);

        await UploadMissingChunksAsync(manifest, openReadAsync, progress, cancellationToken);

        using var completeResponse = await SendWithRetryAsync(
            () => new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/transfers/{manifest.TransferId}/complete"),
            cancellationToken);
        completeResponse.EnsureSuccessStatusCode();
        return manifest.TransferId;
    }

    /// <summary>
    /// Uploads several files behind a single confirmation.
    /// <para>
    /// The files are hashed first so one request can describe all of them; the coordinator answers
    /// with the set it accepted, and only then does anything get transferred. This replaces the
    /// per-file offer and per-file prompt: with a hundred files that used to mean a hundred dialogs
    /// on the desktop, each with its own ten-minute window.
    /// </para>
    /// </summary>
    public async Task<BatchUploadResult> UploadBatchAsync(
        IReadOnlyList<BatchUploadItem> items,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return new BatchUploadResult([], [], []);
        }

        var manifests = new List<FileManifest>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(item.FileName);

            // A staged file already knows its hash, which is what keeps the source from being read
            // twice - and on Android, from being opened twice at all, which some content providers
            // refuse.
            var measured = item.Sha256Hex is { Length: 64 } knownHash && item.Length is { } knownLength
                ? new HashAndLength(knownHash, knownLength)
                : await HashAndMeasureAsync(item.OpenReadAsync, cancellationToken);
            var manifest = new FileManifest(
                Guid.NewGuid(),
                item.FileName,
                measured.Length,
                item.LastModifiedUtc ?? DateTimeOffset.UtcNow,
                ProtocolConstants.DefaultChunkSize,
                measured.Sha256Hex);
            manifest.Validate();
            manifests.Add(manifest);
        }

        var batchId = Guid.NewGuid();
        BatchSubmissionResult decision;
        using (var response = await SendWithRetryAsync(
                   _longRunningClient,
                   () => new HttpRequestMessage(HttpMethod.Post, "/api/v1/batches")
                   {
                       Content = JsonContent.Create(
                           new BatchSubmission(batchId, manifests),
                           options: SerializerOptions)
                   },
                   cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            decision = await BoundedJsonReader.ReadAsync<BatchSubmissionResult>(
                           response.Content,
                           SerializerOptions,
                           cancellationToken)
                       ?? throw new InvalidDataException(
                           "The coordinator returned an empty batch decision.");
        }

        _diagnosticSink?.Invoke(
            $"batch {batchId:N}: accepted {decision.Accepted.Count} of {manifests.Count}");

        var accepted = new HashSet<Guid>(decision.Accepted);
        var completedIndexes = new List<int>();
        var rejectedIndexes = new List<int>();
        var failures = new List<BatchUploadFailure>();

        for (var index = 0; index < manifests.Count; index++)
        {
            var manifest = manifests[index];
            if (!accepted.Contains(manifest.TransferId))
            {
                rejectedIndexes.Add(index);
                continue;
            }

            try
            {
                await UploadMissingChunksAsync(
                    manifest,
                    items[index].OpenReadAsync,
                    progress,
                    cancellationToken);
                using var completeResponse = await SendWithRetryAsync(
                    () => new HttpRequestMessage(
                        HttpMethod.Post,
                        $"/api/v1/transfers/{manifest.TransferId}/complete"),
                    cancellationToken);
                completeResponse.EnsureSuccessStatusCode();
                completedIndexes.Add(index);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One file failing inside an accepted batch must not hide the other results.
                // Full ToString rather than just Message: the Android runtime exception that
                // prompted this logging change carried no usable information without its frames.
                _diagnosticSink?.Invoke($"batch item {manifest.FileName} failed: {exception}");
                failures.Add(new BatchUploadFailure(
                    index,
                    manifest.TransferId,
                    manifest.FileName,
                    exception.Message));
            }
        }

        return new BatchUploadResult(completedIndexes, rejectedIndexes, failures);
    }

    public async Task<IReadOnlyList<RemoteOutboundTransferInfo>> GetAvailableDownloadsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/outbound"),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await BoundedJsonReader.ReadAsync<RemoteOutboundTransferInfo[]>(
                   response.Content,
                   SerializerOptions,
                   cancellationToken)
               ?? [];
    }

    public async Task<string> DownloadAsync(
        RemoteOutboundTransferInfo transfer,
        string destinationDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        transfer.Manifest.Validate();
        if (transfer.DeviceId != _connection.DeviceId)
        {
            throw new UnauthorizedAccessException("The transfer belongs to a different device.");
        }

        var receivedPath = await TryAcknowledgeReceiptAsync(
            transfer,
            destinationDirectory,
            cancellationToken);
        if (receivedPath is not null)
        {
            return receivedPath;
        }

        await using var session = await _receiver.OpenAsync(
            transfer.Manifest,
            destinationDirectory,
            cancellationToken);
        var missing = await _receiver.GetMissingChunksAsync(session, cancellationToken);
        var plan = new ChunkPlan(transfer.Manifest);
        var transferred = transfer.Manifest.Length -
                          missing.Sum(index => (long)plan.GetChunk(index).Length);
        progress?.Report(new TransferProgress(
            transfer.TransferId,
            transfer.Manifest.FileName,
            transferred,
            transfer.Manifest.Length));

        foreach (var index in missing.Order())
        {
            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    $"/api/v1/outbound/{transfer.TransferId}/chunks/{index}"),
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (!response.Headers.TryGetValues("X-Chunk-SHA256", out var hashValues) ||
                hashValues.FirstOrDefault() is not { Length: 64 } chunkHash ||
                !chunkHash.All(char.IsAsciiHexDigit))
            {
                throw new InvalidDataException("The downloaded chunk did not include a valid SHA-256 value.");
            }
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            await _receiver.WriteChunkAsync(session, index, content, chunkHash, cancellationToken);
            transferred += plan.GetChunk(index).Length;
            progress?.Report(new TransferProgress(
                transfer.TransferId,
                transfer.Manifest.FileName,
                transferred,
                transfer.Manifest.Length));
        }

        var finalPath = await _receiver.CompleteAsync(session, cancellationToken);
        await SaveReceiptAsync(transfer, destinationDirectory, finalPath, cancellationToken);
        await AcknowledgeDownloadAsync(transfer.TransferId, cancellationToken);
        DeleteReceipt(transfer.TransferId, destinationDirectory);
        return finalPath;
    }

    public void Dispose()
    {
        _client.Dispose();
        _longRunningClient.Dispose();
    }

    private async Task<RemoteInboundTransferInfo> WaitForApprovalAsync(
        Guid transferId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/transfers/{transferId}"),
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var transfer = await BoundedJsonReader.ReadAsync<RemoteInboundTransferInfo>(
                response.Content,
                SerializerOptions,
                cancellationToken)
                ?? throw new InvalidDataException("The coordinator returned an empty transfer status.");

            if (transfer.State is TransferState.Queued or TransferState.Transferring)
            {
                return transfer;
            }

            if (transfer.State == TransferState.Rejected)
            {
                // Covers both "the user clicked 否" and "the prompt timed out unanswered".
                throw new TransferRejectedException();
            }

            if (transfer.State is TransferState.Failed or TransferState.Canceled)
            {
                throw new InvalidOperationException(transfer.Error ?? $"The transfer is {transfer.State}.");
            }

            await Task.Delay(TransferTuning.TransferPollInterval, cancellationToken);
        }

        throw new TimeoutException("The computer did not approve the transfer before the timeout.");
    }

    private Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken) =>
        SendWithRetryAsync(_client, createRequest, cancellationToken);

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = createRequest();
                var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The device is no longer in the desktop's trusted-device store: the user
                    // removed it, or a newer pairing replaced the credential. Retrying cannot
                    // succeed, and the generic status-code exception is unreadable on a phone,
                    // so surface a dedicated type the UI can turn into "pair again".
                    response.Dispose();
                    throw new CoordinatorCredentialException();
                }

                if (attempt < TransferTuning.TransferMaxAttempts && IsTransient(response.StatusCode))
                {
                    response.Dispose();
                    await Task.Delay(TransferTuning.GetTransferRetryDelay(attempt), cancellationToken);
                    continue;
                }

                return response;
            }
            catch (Exception exception) when (
                attempt < TransferTuning.TransferMaxAttempts &&
                !cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TransferTuning.GetTransferRetryDelay(attempt), cancellationToken);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    /// <summary>
    /// Uploads every chunk the coordinator reports as missing, resuming across rounds until the
    /// transfer is complete or <see cref="TransferTuning.TransferRecoveryBudget"/> runs out.
    /// <para>
    /// The coordinator persists a checkpoint per received chunk, so a transient failure never
    /// invalidates what already arrived. This loop is what turns that capability into an actual
    /// resume: after a failed round the missing set is fetched again and only the remainder is
    /// retransmitted. Without it, one chunk exhausting its quick retries aborted the whole upload
    /// even though a single status request stood between the user and a completed transfer.
    /// </para>
    /// </summary>
    private async Task UploadMissingChunksAsync(
        FileManifest manifest,
        Func<CancellationToken, Task<Stream>> openReadAsync,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = new ChunkPlan(manifest);
        var deadline = DateTimeOffset.UtcNow + TransferTuning.TransferRecoveryBudget;
        await using var source = new SequentialChunkSource(openReadAsync, cancellationToken);
        var transferred = 0L;
        var announced = -1L;
        var lastTransferred = -1L;
        var stalledRounds = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var missing = await GetMissingChunksAsync(manifest.TransferId, cancellationToken);
            if (missing.Count == 0)
            {
                return;
            }

            // Progress is derived from the coordinator's own view, so a resume shows the bytes
            // that actually survived instead of dropping the bar back to zero.
            transferred = manifest.Length - missing.Sum(index => (long)plan.GetChunk(index).Length);
            ReportProgress(manifest, progress, ref announced, transferred);

            var roundCompleted = true;
            foreach (var index in missing.Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = plan.GetChunk(index);
                byte[] chunk;
                try
                {
                    chunk = await source.ReadAsync(descriptor.Offset, descriptor.Length, cancellationToken);
                }
                catch (Exception exception)
                    when (IsTransientFailure(exception) && !cancellationToken.IsCancellationRequested)
                {
                    // The source became unreadable (storage went away, a content provider revoked
                    // access). Treated like a network failure so the next round retries it and the
                    // recovery budget, not this read, decides when to give up.
                    roundCompleted = false;
                    break;
                }

                var chunkHash = Convert.ToHexString(SHA256.HashData(chunk));
                try
                {
                    using var response = await SendWithRetryAsync(
                        () =>
                        {
                            var request = new HttpRequestMessage(
                                HttpMethod.Put,
                                $"/api/v1/transfers/{manifest.TransferId}/chunks/{index}")
                            {
                                Content = new ByteArrayContent(chunk)
                            };
                            request.Headers.Add("X-Chunk-SHA256", chunkHash);
                            return request;
                        },
                        cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        if (!IsTransient(response.StatusCode))
                        {
                            // 400/404/409 mean the coordinator rejected this chunk on purpose
                            // (bad manifest, unknown transfer, wrong state). Retrying cannot fix
                            // that, so surface it instead of looping until the budget expires.
                            response.EnsureSuccessStatusCode();
                        }

                        roundCompleted = false;
                        break;
                    }
                }
                catch (Exception exception)
                    when (IsTransientFailure(exception) && !cancellationToken.IsCancellationRequested)
                {
                    _diagnosticSink?.Invoke($"chunk {index} of {manifest.FileName} failed: {exception}");
                    roundCompleted = false;
                    break;
                }

                transferred += descriptor.Length;
                ReportProgress(manifest, progress, ref announced, transferred);
            }

            if (roundCompleted)
            {
                // Every chunk this round reported as missing was accepted, so the file is complete
                // on the coordinator's side and the caller can move on to the completion call.
                return;
            }

            _diagnosticSink?.Invoke(
                $"resume round for {manifest.FileName}: {missing.Count} chunk(s) still missing, " +
                $"{transferred}/{manifest.Length} bytes on the coordinator");

            // A round that moved nothing means the source itself is unusable - a revoked content
            // URI grant, a file deleted mid-transfer. Waiting out the whole recovery budget for
            // that would hold the UI for half an hour to arrive at the same error.
            if (transferred > lastTransferred)
            {
                lastTransferred = transferred;
                stalledRounds = 0;
            }
            else
            {
                stalledRounds++;
                if (stalledRounds >= TransferTuning.MaxStalledRecoveryRounds)
                {
                    await TryCancelOnCoordinatorAsync(manifest.TransferId);
                    throw new IOException(
                        $"\"{manifest.FileName}\" could not be read after {stalledRounds} attempts " +
                        "without progress.");
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                // Give up cleanly: tell the coordinator to drop the half-written .part file
                // instead of leaving an active transfer behind forever.
                await TryCancelOnCoordinatorAsync(manifest.TransferId);
                throw new TimeoutException(
                    $"The upload did not complete within {TransferTuning.TransferRecoveryBudget}.");
            }

            await WaitForCoordinatorAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Reads the coordinator's view of a transfer, or throws when that view says the transfer can
    /// no longer be resumed. Returning the missing set is what makes the resumable checkpoint
    /// usable from the client side.
    /// </summary>
    private async Task<IReadOnlyList<int>> GetMissingChunksAsync(
        Guid transferId,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/transfers/{transferId}"),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var transfer = await BoundedJsonReader.ReadAsync<RemoteInboundTransferInfo>(
                           response.Content,
                           SerializerOptions,
                           cancellationToken)
                       ?? throw new InvalidDataException("The coordinator returned an empty transfer status.");

        return transfer.State switch
        {
            TransferState.Completed => Array.Empty<int>(),
            TransferState.Rejected => throw new TransferRejectedException(),
            TransferState.Canceled => throw new OperationCanceledException("The transfer was canceled."),
            TransferState.Failed => throw new InvalidOperationException(
                transfer.Error ?? "The transfer failed on the coordinator."),
            _ => transfer.MissingChunks
        };
    }

    /// <summary>
    /// Waits out the current failure, then keeps probing the coordinator's health endpoint until
    /// it answers again. Probing by interval stops the recovery loop from spinning while the Wi-Fi
    /// is down, and gives the transfer a definite point at which resuming makes sense.
    /// </summary>
    private async Task WaitForCoordinatorAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TransferTuning.TransferRecoveryRetryDelay, cancellationToken);

            try
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probe.CancelAfter(TransferTuning.RecoveryProbeTimeout);
                using var response = await _client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, "/api/v1/health"),
                    HttpCompletionOption.ResponseHeadersRead,
                    probe.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception exception)
                when (exception is HttpRequestException or OperationCanceledException &&
                      !cancellationToken.IsCancellationRequested)
            {
                // Still offline, or the probe timed out. Keep waiting.
            }
        }
    }

    private static void ReportProgress(
        FileManifest manifest,
        IProgress<TransferProgress>? progress,
        ref long announced,
        long transferred)
    {
        if (progress is null || transferred == announced)
        {
            return;
        }

        announced = transferred;
        progress.Report(new TransferProgress(
            manifest.TransferId,
            manifest.FileName,
            transferred,
            manifest.Length));
    }

    private static bool IsTransientFailure(Exception exception) =>
        exception is HttpRequestException or IOException or TimeoutException or OperationCanceledException

        // An exception raised on the Java side - a content provider refusing to reopen a stream, a
        // revoked URI grant - arrives here as a Java runtime exception. Treating it as a failed
        // attempt gives the recovery loop a chance to reopen the source instead of failing the file
        // outright on the first hiccup.
        || exception.GetType().FullName?.StartsWith("Java.", StringComparison.Ordinal) == true;

    /// <summary>
    /// Best-effort cleanup of a transfer this client has given up on, so the coordinator does not
    /// keep a half-written <c>.part</c> file and an active state around forever.
    /// </summary>
    private async Task TryCancelOnCoordinatorAsync(Guid transferId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TransferTuning.RecoveryProbeTimeout);
            using var response = await _client.SendAsync(
                new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/transfers/{transferId}"),
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            // The coordinator is unreachable or has already forgotten the transfer.
        }
    }

    private async Task AcknowledgeDownloadAsync(Guid transferId, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/outbound/{transferId}/complete"),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string?> TryAcknowledgeReceiptAsync(
        RemoteOutboundTransferInfo transfer,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var receiptPath = GetReceiptPath(transfer.TransferId, destinationDirectory);
        if (!File.Exists(receiptPath))
        {
            return null;
        }

        var receipt = JsonSerializer.Deserialize<DownloadReceipt>(
            await File.ReadAllTextAsync(receiptPath, cancellationToken),
            SerializerOptions);
        if (receipt is null ||
            !string.Equals(receipt.Sha256Hex, transfer.Manifest.Sha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The completed download receipt is invalid.");
        }

        var finalPath = Path.Combine(Path.GetFullPath(destinationDirectory), Path.GetFileName(receipt.FinalFileName));
        if (!File.Exists(finalPath))
        {
            throw new FileNotFoundException("The completed download file is missing.", finalPath);
        }

        await AcknowledgeDownloadAsync(transfer.TransferId, cancellationToken);
        File.Delete(receiptPath);
        return finalPath;
    }

    private static async Task SaveReceiptAsync(
        RemoteOutboundTransferInfo transfer,
        string destinationDirectory,
        string finalPath,
        CancellationToken cancellationToken)
    {
        var receiptPath = GetReceiptPath(transfer.TransferId, destinationDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        var temporaryPath = $"{receiptPath}.new";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(
                    new DownloadReceipt(Path.GetFileName(finalPath), transfer.Manifest.Sha256Hex),
                    SerializerOptions),
                cancellationToken);
            File.Move(temporaryPath, receiptPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void DeleteReceipt(Guid transferId, string destinationDirectory) =>
        File.Delete(GetReceiptPath(transferId, destinationDirectory));

    private static string GetReceiptPath(Guid transferId, string destinationDirectory) =>
        Path.Combine(
            Path.GetFullPath(destinationDirectory),
            ".localtransfer",
            $"{transferId:N}.received.json");

    private static async Task<HashAndLength> HashAndMeasureAsync(
        Func<CancellationToken, Task<Stream>> openReadAsync,
        CancellationToken cancellationToken)
    {
        await using var source = await openReadAsync(cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long length = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) != 0)
            {
                hash.AppendData(buffer, 0, read);
                length = checked(length + read);
            }

            return new HashAndLength(Convert.ToHexString(hash.GetHashAndReset()), length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed record HashAndLength(string Sha256Hex, long Length);

    private sealed record DownloadReceipt(string FinalFileName, string Sha256Hex);

    private sealed class SequentialChunkSource : IAsyncDisposable
    {
        private readonly Func<CancellationToken, Task<Stream>> _openReadAsync;
        private Stream? _stream;
        private long _position;

        public SequentialChunkSource(
            Func<CancellationToken, Task<Stream>> openReadAsync,
            CancellationToken cancellationToken)
        {
            _openReadAsync = openReadAsync;
            InitialCancellationToken = cancellationToken;
        }

        private CancellationToken InitialCancellationToken { get; }

        public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken)
        {
            _stream ??= await _openReadAsync(InitialCancellationToken);
            if (_stream.CanSeek)
            {
                _stream.Position = offset;
                _position = offset;
            }
            else
            {
                if (offset < _position)
                {
                    await _stream.DisposeAsync();
                    _stream = await _openReadAsync(cancellationToken);
                    _position = 0;
                }

                await SkipAsync(_stream, offset - _position, cancellationToken);
                _position = offset;
            }

            var content = new byte[length];
            await _stream.ReadExactlyAsync(content, cancellationToken);
            _position += length;
            return content;
        }

        public async ValueTask DisposeAsync()
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync();
            }
        }

        private static async Task SkipAsync(Stream stream, long length, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                var remaining = length;
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                        cancellationToken);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("The source file ended before the expected chunk offset.");
                    }

                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
