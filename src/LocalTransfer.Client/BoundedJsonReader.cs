using System.Buffers;
using System.Text.Json;

namespace LocalTransfer.Client;

internal static class BoundedJsonReader
{
    // SendWithRetryAsync uses HttpCompletionOption.ResponseHeadersRead, so HttpClient's
    // MaxResponseContentBufferSize never applies; enforce the limit here instead.
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    public static async Task<T?> ReadAsync<T>(
        HttpContent content,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declaredLength && declaredLength > MaxResponseBytes)
        {
            throw new InvalidDataException("The coordinator response exceeds the allowed size.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var bounded = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (bounded.Length + read > MaxResponseBytes)
                {
                    throw new InvalidDataException("The coordinator response exceeds the allowed size.");
                }

                bounded.Write(buffer, 0, read);
            }

            return JsonSerializer.Deserialize<T>(bounded.ToArray(), options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
