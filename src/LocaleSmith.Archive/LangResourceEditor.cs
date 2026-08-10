using System.Text;

namespace LocaleSmith.Archive;

internal static class LangResourceEditor
{
    public static IReadOnlyList<(string Key, string Value)> ReadEntries(string text)
    {
        var entries = new List<(string Key, string Value)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in EnumerateLines(text))
        {
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] is '#' or '!')
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            if (key.Length > 0 && keys.Add(key))
            {
                entries.Add((key, line[(separator + 1)..]));
            }
        }

        return entries;
    }

    public static byte[] Update(
        string baseText,
        IReadOnlyDictionary<string, string> translations)
    {
        string newline = baseText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool trailingNewline = baseText.EndsWith('\n');
        string[] lines = baseText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var remaining = new Dictionary<string, string>(translations, StringComparer.Ordinal);

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] is '#' or '!')
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            if (remaining.Remove(key, out string? translated))
            {
                lines[index] = $"{line[..(separator + 1)]}{translated}";
            }
        }

        var output = new List<string>(lines);
        if (!trailingNewline && output.Count > 0 && output[^1].Length == 0)
        {
            output.RemoveAt(output.Count - 1);
        }

        foreach ((string key, string value) in remaining.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            output.Add($"{key}={value}");
        }

        string result = string.Join(newline, output);
        if (trailingNewline && !result.EndsWith(newline, StringComparison.Ordinal))
        {
            result += newline;
        }

        return Encoding.UTF8.GetBytes(result);
    }

    private static string[] EnumerateLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
