using System.Buffers;
using System.Security.Cryptography;

namespace LocalTransfer.Mobile;

/// <summary>
/// A source file prepared for upload: an ordinary local file the pipeline can open as often as it
/// needs, plus the SHA-256 computed while it was copied.
/// </summary>
internal sealed class StagedSourceFile : IDisposable
{
    private readonly string? _stagingDirectory;

    internal StagedSourceFile(
        string? stagingDirectory,
        Func<CancellationToken, Task<Stream>> openReadAsync,
        string? sha256Hex = null,
        long? length = null)
    {
        _stagingDirectory = stagingDirectory;
        OpenReadAsync = openReadAsync;
        Sha256Hex = sha256Hex;
        Length = length;
    }

    /// <summary>Opens the payload. Called several times per upload, so it must be repeatable.</summary>
    public Func<CancellationToken, Task<Stream>> OpenReadAsync { get; }

    /// <summary>Computed while staging, so the upload never reads the source a second time.</summary>
    public string? Sha256Hex { get; }

    public long? Length { get; }

    public void Dispose()
    {
        if (_stagingDirectory is null)
        {
            return;
        }

        try
        {
            Directory.Delete(_stagingDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover staging folder is harmless; the OS reclaims cache space on its own.
        }
    }
}

/// <summary>
/// Makes a picked file safe to upload by copying it into the app cache first.
/// <para>
/// The upload pipeline opens a source more than once - once to hash it, again to send it - and an
/// Android content URI is typically only valid for one provider session, often refusing to be
/// reopened at all. A WeChat/QQ picker URI throwing <c>java.lang.RuntimeException</c> on the second
/// open is exactly what made an otherwise approved upload fail. Copying once removes that whole
/// class of failure and leaves the pipeline with a file it can seek in.
/// </para>
/// <para>
/// The SHA-256 is computed during the copy, so nothing has to read the source twice either.
/// </para>
/// </summary>
internal static class UploadStaging
{
    private const string StagingFolderName = "upload-staging";

    private const int CopyBufferSize = 128 * 1024;

    public static async Task<StagedSourceFile> PrepareAsync(
        SharedSourceFile file,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        var directory = Path.Combine(
            FileSystem.CacheDirectory,
            StagingFolderName,
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(directory);
            var payloadPath = Path.Combine(directory, "payload.bin");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var length = 0L;
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                await using var source = await file.OpenReadAsync(cancellationToken);
                await using var target = new FileStream(
                    payloadPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                int read;
                while ((read = await source.ReadAsync(
                           buffer.AsMemory(0, buffer.Length),
                           cancellationToken)) != 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    length += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new StagedSourceFile(
                directory,
                _ => Task.FromResult<Stream>(OpenPayload(payloadPath)),
                Convert.ToHexString(hash.GetHashAndReset()),
                length);
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(directory);
            throw;
        }
        catch (Exception exception)
        {
            // Copying can fail for reasons unrelated to the upload - a full cache partition is the
            // common one. Fall back to streaming the source directly so a file that cannot be
            // staged still has a chance, and leave a trace of why it was not staged.
            TryDeleteDirectory(directory);
            MobileDiagnostics.Log("staging", exception);
            return new StagedSourceFile(null, file.OpenReadAsync);
        }
    }

    private static Stream OpenPayload(string payloadPath) =>
        new FileStream(
            payloadPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing else to try; the cache is disposable by nature.
        }
    }
}
