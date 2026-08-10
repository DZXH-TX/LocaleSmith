namespace JaxI18n.Core.Models;

public enum TranslationStyle
{
    Formal,
    Informal
}

public sealed record TranslationEntry
{
    public TranslationEntry(string relativePath, string? key, string sourceText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(sourceText);
        RelativePath = relativePath.Replace('\\', '/').TrimStart('/');
        Key = string.IsNullOrWhiteSpace(key) ? null : key;
        SourceText = sourceText;
    }

    public string RelativePath { get; }

    public string? Key { get; }

    public string SourceText { get; }

    public string StableId => $"{RelativePath}\0{Key ?? string.Empty}";
}

public sealed record TranslationBatchRequest
{
    public TranslationBatchRequest(
        IReadOnlyList<TranslationEntry> entries,
        string targetLanguage = "zh_CN",
        IReadOnlySet<TranslationStyle>? styles = null,
        string? modelSourceId = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        if (entries.Any(static entry => entry is null))
        {
            throw new ArgumentException("Translation entries cannot contain null values.", nameof(entries));
        }

        Entries = entries.ToArray();
        TargetLanguage = targetLanguage.Trim();
        ModelSourceId = string.IsNullOrWhiteSpace(modelSourceId) ? null : modelSourceId.Trim();
        Styles = styles is null
            ? new HashSet<TranslationStyle> { TranslationStyle.Formal }
            : new HashSet<TranslationStyle>(styles);

        if (Styles.Count != 1 || !Enum.IsDefined(Styles.Single()))
        {
            throw new ArgumentException("Exactly one valid output style is required per translation request.", nameof(styles));
        }
    }

    public IReadOnlyList<TranslationEntry> Entries { get; }

    public string TargetLanguage { get; }

    public IReadOnlySet<TranslationStyle> Styles { get; }

    /// <summary>
    /// Captures the model source chosen when work is queued so later UI selection changes do not
    /// alter an already accepted translation request.
    /// </summary>
    public string? ModelSourceId { get; }
}

public sealed record TranslationVariant(TranslationStyle Style, string Text);

public sealed record TranslatedEntry(
    string RelativePath,
    string? Key,
    string SourceHash,
    IReadOnlyList<TranslationVariant> Variants);

public sealed record TranslationBatchResult(
    string TargetLanguage,
    IReadOnlyList<TranslatedEntry> Entries);
