namespace LocalTransfer.Core.Transfers;

public static class FileNamePolicy
{
    private const int DefaultMaximumLength = 180;
    private static readonly HashSet<char> InvalidCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Sanitize(string fileName, int maximumLength = DefaultMaximumLength)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("A file name is required.", nameof(fileName));
        }

        if (maximumLength < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        var sanitized = new string(fileName
            .Select(character => InvalidCharacters.Contains(character) ||
                                 char.IsControl(character) ||
                                 // Format characters (bidi overrides, word joiners, BOM) are
                                 // invisible but can spoof how a file name renders in the UI.
                                 char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format
                ? '_'
                : character)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "unnamed";
        }

        // Windows opens a device instead of a file when the stem is a reserved name (CON, NUL, COM1, ...).
        if (ReservedDeviceNames.Contains(Path.GetFileNameWithoutExtension(sanitized)))
        {
            sanitized = $"file_{sanitized}";
        }

        if (sanitized.Length <= maximumLength)
        {
            return sanitized;
        }

        var extension = Path.GetExtension(sanitized);
        if (extension.Length >= maximumLength - 4)
        {
            extension = string.Empty;
        }

        var stemLength = maximumLength - extension.Length;
        return string.Concat(sanitized.AsSpan(0, stemLength).TrimEnd(), extension);
    }

    public static string GetAvailablePath(string directory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var sanitized = Sanitize(fileName);
        var candidate = Path.Combine(directory, sanitized);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var extension = Path.GetExtension(sanitized);
        var stem = Path.GetFileNameWithoutExtension(sanitized);
        for (var index = 1; index < int.MaxValue; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("No available destination file name could be generated.");
    }
}
