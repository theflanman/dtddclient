namespace DoesTheDogDie.Tests.Support;

/// <summary>
/// Minimal parser for <c>.env</c> files used by tests to locate a locally
/// configured <c>DTDD_API_KEY</c> without requiring it to be exported as a
/// process environment variable.
/// </summary>
internal static class DotEnv
{
    /// <summary>
    /// Parses <c>KEY=VALUE</c> lines from the given text.
    /// Blank lines and lines starting with <c>#</c> are skipped. Lines
    /// without an <c>=</c> are ignored. Keys and values are trimmed of
    /// surrounding whitespace, and a value wrapped in matching double
    /// quotes has the quotes stripped. If a key appears more than once,
    /// the last occurrence wins.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Walks upward from <paramref name="startDir"/> looking for a file
    /// named <paramref name="name"/>, returning its full path or
    /// <c>null</c> if none of the ancestor directories contain it.
    /// </summary>
    public static string? FindFile(string startDir, string name = ".env")
    {
        var directory = new DirectoryInfo(startDir);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
