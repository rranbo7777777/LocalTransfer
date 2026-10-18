using System.Text.Json;

namespace LocalTransfer.Coordinator.History;

public enum TransferDirection
{
    Inbound,
    Outbound
}

public enum TransferOutcome
{
    Completed,
    Failed,
    Rejected,
    Canceled
}

/// <summary>One finished transfer, as recorded in the history log.</summary>
public sealed record TransferHistoryEntry(
    Guid TransferId,
    Guid DeviceId,
    TransferDirection Direction,
    string FileName,
    long Length,
    DateTimeOffset AtUtc,
    TransferOutcome Outcome,
    string? Error);

/// <summary>
/// Append-only record of every transfer that reached a terminal state.
/// <para>
/// JSON Lines rather than one JSON array: a crash mid-write costs the last line instead of the whole
/// file, and recording a result never rewrites what came before. The history page used to be built
/// by scanning the receive directory, which could only ever show one direction and lost a file the
/// moment the user moved or renamed it.
/// </para>
/// </summary>
public sealed class TransferHistoryStore
{
    private const int MaxEntries = 500;
    private const long MaxBytes = 512 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();

    private readonly string _path;

    public TransferHistoryStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "history.jsonl");
    }

    public string FilePath => _path;

    public void Append(TransferHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            var line = JsonSerializer.Serialize(entry, SerializerOptions) + Environment.NewLine;
            lock (_gate)
            {
                File.AppendAllText(_path, line);
                TrimLocked();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; it must never fail the transfer that produced it.
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<TransferHistoryEntry> Read(int maxEntries = 200)
    {
        if (maxEntries <= 0)
        {
            return [];
        }

        try
        {
            string[] lines;
            lock (_gate)
            {
                if (!File.Exists(_path))
                {
                    return [];
                }

                lines = File.ReadAllLines(_path);
            }

            var entries = new List<TransferHistoryEntry>(lines.Length);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<TransferHistoryEntry>(line, SerializerOptions);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // A torn last line from a crash is skipped, not fatal.
                }
            }

            entries.Reverse();
            return entries.Count > maxEntries ? entries.GetRange(0, maxEntries) : entries;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Keeps the file bounded by rewriting it with the newest entries.</summary>
    private void TrimLocked()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length <= MaxBytes)
        {
            return;
        }

        var lines = File.ReadAllLines(_path);
        File.WriteAllLines(
            _path,
            lines.Skip(Math.Max(0, lines.Length - MaxEntries)).ToArray());
    }
}
