using System.Text.RegularExpressions;

namespace LocaleSmith.Application.Services;

internal static partial class PlaceholderValidator
{
    public static void EnsurePreserved(string source, string translation, string entryId, string style)
    {
        var sourceTokens = PlaceholderPattern()
            .Matches(source)
            .Select(static match => match.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var translatedTokens = PlaceholderPattern()
            .Matches(translation)
            .Select(static match => match.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (!sourceTokens.SequenceEqual(translatedTokens, StringComparer.Ordinal))
        {
            throw new TranslationContractException(
                $"Translation '{entryId}' ({style}) changed formatting placeholders.");
        }

        if (CountLogicalLineBreaks(source) != CountLogicalLineBreaks(translation))
        {
            throw new TranslationContractException(
                $"Translation '{entryId}' ({style}) changed the source line structure.");
        }
    }

    private static int CountLogicalLineBreaks(string value)
    {
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\n')
            {
                count++;
            }
            else if (value[index] == '\r')
            {
                count++;
                if (index + 1 < value.Length && value[index + 1] == '\n')
                {
                    index++;
                }
            }
        }

        return count;
    }

    [GeneratedRegex(
        "%(?:\\d+\\$)?[-#+0,(<]*\\d*(?:\\.\\d+)?[tT]?[a-zA-Z%]|\\{\\d+(?:,[^}]*)?\\}|§[0-9a-fk-or]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
