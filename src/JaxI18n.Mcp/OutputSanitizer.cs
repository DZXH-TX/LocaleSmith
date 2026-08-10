using System.Text;
using System.Text.RegularExpressions;

namespace JaxI18n.Mcp;

internal static partial class OutputSanitizer
{
    public static string Sanitize(string? value, int maximumCharacters)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var withoutAnsi = AnsiSequence().Replace(value, string.Empty);
        var redacted = SensitiveAssignment().Replace(withoutAnsi, static match => match.Groups[1].Value + "***REDACTED***");
        var builder = new StringBuilder(Math.Min(redacted.Length, maximumCharacters));
        foreach (var character in redacted)
        {
            if (builder.Length >= maximumCharacters)
            {
                break;
            }

            if (character is '\r' or '\n' or '\t' || !char.IsControl(character))
            {
                builder.Append(character == '\r' ? '\n' : character);
            }
        }

        if (redacted.Length > maximumCharacters)
        {
            builder.Append("\n[output truncated]");
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"(?:\x1B\[[0-?]*[ -/]*[@-~])|(?:\x1B\][^\x07]*(?:\x07|\x1B\\))", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AnsiSequence();

    [GeneratedRegex(@"(?im)\b(api[-_]?key|token|secret|password|credential)\s*[:=]\s*([^\s,;]+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SensitiveAssignment();
}
