using System.Text;

namespace LocalTransfer.Mobile;

/// <summary>
/// Field diagnostics. An exception raised on the Java side and escaping into a Java-initiated
/// callback (Activity lifecycle, Camera2, a third-party content provider) reaches managed code
/// as an opaque <c>java.lang.RuntimeException: exception_wasthrown</c> whose message says nothing
/// about the cause, and the only record the user has is a one-line alert box. Append the real
/// exception - message, inner exceptions and stack frames - to a log that can be copied off the
/// handset over USB.
/// </summary>
internal static class MobileDiagnostics
{
    private const string FileName = "localtransfer-error.log";

    private static readonly object Gate = new();

    /// <summary>Full path of the log, or <c>null</c> when storage is not usable yet.</summary>
    public static string? TryGetLogPath()
    {
        try
        {
            return Path.Combine(GetLogDirectory(), FileName);
        }
        catch
        {
            return null;
        }
    }

    public static void Log(string context, Exception exception)
    {
        try
        {
            var path = TryGetLogPath();
            if (path is null)
            {
                return;
            }

            var entry = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))
                .Append(" [").Append(context).AppendLine("]")
                .AppendLine(exception.ToString())
                .AppendLine(new string('-', 72))
                .ToString();

            lock (Gate)
            {
                File.AppendAllText(path, entry);
            }
        }
        catch
        {
            // Diagnostics must never take the app down; there is nothing useful left to try.
        }
    }

    private static string GetLogDirectory()
    {
#if ANDROID
        // GetExternalFilesDir(null) is /storage/emulated/0/Android/data/<pkg>/files, which the
        // phone owner can pull off over USB. AppDataDirectory is app-private and unreachable.
        var external = Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
        if (!string.IsNullOrEmpty(external))
        {
            return external;
        }
#endif
        return FileSystem.AppDataDirectory;
    }
}
