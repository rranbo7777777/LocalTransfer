using System.Collections.Concurrent;

namespace LocalTransfer.Mobile;

public sealed record SharedSourceFile(
    string FileName,
    string ContentType,
    long? Length,
    Func<CancellationToken, Task<Stream>> OpenReadAsync);

public static class SharedFileInbox
{
    private static readonly ConcurrentQueue<SharedSourceFile> Queue = new();

    public static event EventHandler? FilesAvailable;

    public static void Add(IEnumerable<SharedSourceFile> files)
    {
        var added = false;
        foreach (var file in files)
        {
            Queue.Enqueue(file);
            added = true;
        }

        if (added)
        {
            FilesAvailable?.Invoke(null, EventArgs.Empty);
        }
    }

    public static IReadOnlyList<SharedSourceFile> Drain()
    {
        var files = new List<SharedSourceFile>();
        while (Queue.TryDequeue(out var file))
        {
            files.Add(file);
        }

        return files;
    }
}
