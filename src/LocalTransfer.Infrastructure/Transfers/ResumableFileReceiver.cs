using System.Buffers;
using System.Security.Cryptography;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Core.Transfers;

namespace LocalTransfer.Infrastructure.Transfers;

public sealed class ResumableFileReceiver
{
    private const int BufferSize = 128 * 1024;
    private const string WorkingDirectoryName = ".localtransfer";
    private readonly TransferCheckpointStore _checkpointStore = new();

    public async Task<ReceiveSession> OpenAsync(
        FileManifest manifest,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        manifest.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var fullDestinationDirectory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(fullDestinationDirectory);

        var workingDirectory = Path.Combine(fullDestinationDirectory, WorkingDirectoryName);
        Directory.CreateDirectory(workingDirectory);

        var transferName = manifest.TransferId.ToString("N");
        var checkpointPath = Path.Combine(workingDirectory, $"{transferName}.json");
        var temporaryPath = Path.Combine(workingDirectory, $"{transferName}.part");

        var checkpoint = await _checkpointStore.LoadAsync(checkpointPath, cancellationToken);
        if (checkpoint is null)
        {
            EnsureAvailableSpace(fullDestinationDirectory, manifest.Length);
            var finalPath = FileNamePolicy.GetAvailablePath(fullDestinationDirectory, manifest.FileName);
            checkpoint = new TransferCheckpoint(
                manifest,
                Path.GetFileName(finalPath),
                []);
            await _checkpointStore.SaveAsync(checkpointPath, checkpoint, cancellationToken);
        }
        else
        {
            ValidateCheckpoint(manifest, checkpoint);
        }

        await EnsureTemporaryFileAsync(temporaryPath, manifest.Length, cancellationToken);
        return new ReceiveSession(
            manifest,
            fullDestinationDirectory,
            temporaryPath,
            checkpointPath,
            checkpoint);
    }

    public async Task<IReadOnlyList<int>> GetMissingChunksAsync(
        ReceiveSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            return Enumerable.Range(0, session.Manifest.ChunkCount)
                .Where(index => !session.Checkpoint.CompletedChunks.Contains(index))
                .ToArray();
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task WriteChunkAsync(
        ReceiveSession session,
        int chunkIndex,
        Stream content,
        string expectedChunkSha256Hex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(content);
        ValidateSha256(expectedChunkSha256Hex, nameof(expectedChunkSha256Hex));

        var chunk = new ChunkPlan(session.Manifest).GetChunk(chunkIndex);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.Checkpoint.CompletedChunks.Contains(chunkIndex))
            {
                return;
            }

            if (content.CanSeek && content.Length - content.Position != chunk.Length)
            {
                throw new InvalidDataException("Chunk content length does not match the manifest.");
            }

            await using var target = new FileStream(
                session.TemporaryPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            target.Position = chunk.Offset;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                var remaining = chunk.Length;
                while (remaining > 0)
                {
                    var read = await content.ReadAsync(
                        buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                        cancellationToken);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("Chunk ended before the expected length was received.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    remaining -= read;
                }

                if (!content.CanSeek && await content.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
                {
                    throw new InvalidDataException("Chunk contains more data than declared.");
                }

                var actualHash = hash.GetHashAndReset();
                if (!CryptographicOperations.FixedTimeEquals(
                    actualHash,
                    Convert.FromHexString(expectedChunkSha256Hex)))
                {
                    throw new InvalidDataException("Chunk SHA-256 verification failed.");
                }

                await target.FlushAsync(cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var completedChunks = new HashSet<int>(session.Checkpoint.CompletedChunks)
            {
                chunkIndex
            };
            session.Checkpoint = session.Checkpoint with { CompletedChunks = completedChunks };
            await _checkpointStore.SaveAsync(session.CheckpointPath, session.Checkpoint, cancellationToken);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<string> CompleteAsync(
        ReceiveSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.Checkpoint.CompletedChunks.Count != session.Manifest.ChunkCount)
            {
                throw new InvalidOperationException("The transfer cannot complete while chunks are missing.");
            }

            byte[] actualHash;
            await using (var stream = new FileStream(
                session.TemporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
            }

            if (!CryptographicOperations.FixedTimeEquals(
                actualHash,
                Convert.FromHexString(session.Manifest.Sha256Hex)))
            {
                throw new InvalidDataException("File SHA-256 verification failed.");
            }

            var finalPath = Path.Combine(session.DestinationDirectory, session.Checkpoint.FinalFileName);
            if (File.Exists(finalPath) || Directory.Exists(finalPath))
            {
                finalPath = FileNamePolicy.GetAvailablePath(
                    session.DestinationDirectory,
                    session.Checkpoint.FinalFileName);
            }

            File.Move(session.TemporaryPath, finalPath, overwrite: false);
            File.SetLastWriteTimeUtc(finalPath, session.Manifest.LastModifiedUtc.UtcDateTime);
            File.Delete(session.CheckpointPath);
            return finalPath;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task CancelAsync(
        ReceiveSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            File.Delete(session.TemporaryPath);
            File.Delete(session.CheckpointPath);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static async Task EnsureTemporaryFileAsync(
        string temporaryPath,
        long length,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            temporaryPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.Asynchronous);
        if (stream.Length != length)
        {
            stream.SetLength(length);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private static void ValidateCheckpoint(FileManifest manifest, TransferCheckpoint checkpoint)
    {
        if (checkpoint.Manifest != manifest)
        {
            throw new InvalidDataException("The existing checkpoint does not match the transfer manifest.");
        }

        if (checkpoint.CompletedChunks.Any(index => index < 0 || index >= manifest.ChunkCount))
        {
            throw new InvalidDataException("The existing checkpoint contains an invalid chunk index.");
        }
    }

    private static void EnsureAvailableSpace(string directory, long requiredBytes)
    {
        if (requiredBytes == 0)
        {
            return;
        }

        var root = Path.GetPathRoot(directory);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
        {
            throw new IOException("The destination does not have enough available space.");
        }
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("A 32-byte SHA-256 value is required.", parameterName);
        }
    }
}
