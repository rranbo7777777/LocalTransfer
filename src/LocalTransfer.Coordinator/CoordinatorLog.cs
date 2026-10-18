using System.Text;

namespace LocalTransfer.Coordinator;

/// <summary>
/// Append-only diagnostics for the desktop service.
/// <para>
/// The service runs unattended, and the only symptom of a failed transfer is a line in the phone's
/// alert box - which the desktop side never sees. Recording offers, approvals, state transitions
/// and chunk failures under the data directory is what makes "why did this transfer stop"
/// answerable afterwards. Every write is best-effort: diagnostics must never be the reason a
/// transfer fails.
/// </para>
/// </summary>
internal static class CoordinatorLog
{
    private const long MaxBytes = 4 * 1024 * 1024;

    private static readonly object Gate = new();

    private static string? _path;

    /// <summary>Points the log at <paramref name="dataDirectory"/>. Safe to call more than once.</summary>
    public static void Initialize(string dataDirectory)
    {
        try
        {
            var directory = Path.Combine(dataDirectory, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "coordinator.log");
            Rotate(path);
            _path = path;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Fall back to no logging rather than refusing to start.
            _path = null;
        }
    }

    /// <summary>The current log file, or <c>null</c> when logging is unavailable.</summary>
    public static string? CurrentPath => _path;

    public static void Write(string message)
    {
        var path = _path;
        if (path is null)
        {
            return;
        }

        try
        {
            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append("  ")
                .AppendLine(message)
                .ToString();

            lock (Gate)
            {
                File.AppendAllText(path, line);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort by design.
        }
    }

    /// <summary>
    /// Keeps one previous log, so restarting the service does not erase the evidence of whatever
    /// went wrong just before it.
    /// </summary>
    private static void Rotate(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < MaxBytes)
            {
                return;
            }

            File.Move(path, path + ".1", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
