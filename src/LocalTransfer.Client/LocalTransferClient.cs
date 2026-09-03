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
    private readonly ResumableFileReceiver _receiver = new();

    public LocalTransferClient(CoordinatorConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _client = PinnedHttpClientFactory.Create(connection.Endpoint, connection.CertificateSha256);
        _client.DefaultRequestHeaders.Add("X-LocalTransfer-Device", connection.DeviceId.ToString());
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", connection.Credential);
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

        var status = await WaitForApprovalAsync(
            manifest.TransferId,
            approvalTimeout ?? TimeSpan.FromMinutes(10),
            cancellationToken);
        var plan = new ChunkPlan(manifest);
        var missing = status.MissingChunks.Order().ToArray();
        var transferred = manifest.Length - missing.Sum(index => (long)plan.GetChunk(index).Length);
        progress?.Report(new TransferProgress(manifest.TransferId, manifest.FileName, transferred, manifest.Length));

        await using var source = new SequentialChunkSource(openReadAsync, cancellationToken);
        foreach (var index in missing)
        {
            var descriptor = plan.GetChunk(index);
            var chunk = await source.ReadAsync(descriptor.Offset, descriptor.Length, cancellationToken);
            var chunkHash = Convert.ToHexString(SHA256.HashData(chunk));
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
            response.EnsureSuccessStatusCode();
            transferred += descriptor.Length;
            progress?.Report(new TransferProgress(manifest.TransferId, manifest.FileName, transferred, manifest.Length));
        }

        using var completeResponse = await SendWithRetryAsync(
            () => new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/transfers/{manifest.TransferId}/complete"),
            cancellationToken);
        completeResponse.EnsureSuccessStatusCode();
        return manifest.TransferId;
    }

    public async Task<IReadOnlyList<RemoteOutboundTransferInfo>> GetAvailableDownloadsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/outbound"),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RemoteOutboundTransferInfo[]>(
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
            if (!response.Headers.TryGetValues("X-Chunk-SHA256", out var hashValues))
            {
                throw new InvalidDataException("The downloaded chunk did not include a SHA-256 value.");
            }

            var chunkHash = hashValues.Single();
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

    public void Dispose() => _client.Dispose();

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
            var transfer = await response.Content.ReadFromJsonAsync<RemoteInboundTransferInfo>(
                SerializerOptions,
                cancellationToken)
                ?? throw new InvalidDataException("The coordinator returned an empty transfer status.");

            if (transfer.State is TransferState.Queued or TransferState.Transferring)
            {
                return transfer;
            }

            if (transfer.State == TransferState.Rejected)
            {
                throw new UnauthorizedAccessException("The computer rejected the file transfer.");
            }

            if (transfer.State is TransferState.Failed or TransferState.Canceled)
            {
                throw new InvalidOperationException(transfer.Error ?? $"The transfer is {transfer.State}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
        }

        throw new TimeoutException("The computer did not approve the transfer before the timeout.");
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = createRequest();
                var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (attempt < 3 && IsTransient(response.StatusCode))
                {
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
                    continue;
                }

                return response;
            }
            catch (Exception exception) when (
                attempt < 3 &&
                !cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

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
