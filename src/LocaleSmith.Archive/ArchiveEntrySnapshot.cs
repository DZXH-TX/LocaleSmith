namespace LocaleSmith.Archive;

internal sealed record ArchiveEntrySnapshot(
    int Index,
    string ArchivePath,
    bool IsDirectory,
    DateTimeOffset LastWriteTime,
    int ExternalAttributes,
    CompressionKind Compression,
    string ExtractedPath);

internal enum CompressionKind
{
    Stored,
    Compressed
}

internal enum TranslatableResourceKind
{
    LanguageJson,
    ExternalizedLanguageJson,
    LanguageLang,
    PackText,
    Mcmeta
}

internal sealed record SourceEntryDescriptor(
    string StableId,
    string RelativePath,
    string? Key,
    string SourceText,
    TranslatableResourceKind Kind,
    string TargetArchivePath);

internal sealed record ExternalizedSourceDescriptor(
    string ClassArchivePath,
    string TranslationKey,
    string OriginalText,
    string TargetArchivePath);
