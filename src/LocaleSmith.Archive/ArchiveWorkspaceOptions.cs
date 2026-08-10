namespace LocaleSmith.Archive;

public sealed record ArchiveWorkspaceOptions
{
    public const int DefaultMaximumEntryCount = 50_000;
    public const int DefaultMaximumDirectoryDepth = 64;
    public const long DefaultMaximumEntryBytes = 512L * 1024 * 1024;
    public const long DefaultMaximumTotalBytes = 4L * 1024 * 1024 * 1024;

    public int MaximumEntryCount { get; init; } = DefaultMaximumEntryCount;

    public int MaximumDirectoryDepth { get; init; } = DefaultMaximumDirectoryDepth;

    public long MaximumEntryBytes { get; init; } = DefaultMaximumEntryBytes;

    public long MaximumTotalBytes { get; init; } = DefaultMaximumTotalBytes;

    internal void Validate()
    {
        if (MaximumEntryCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumEntryCount),
                "The maximum entry count must be greater than zero.");
        }

        if (MaximumDirectoryDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumDirectoryDepth),
                "The maximum directory depth must be greater than zero.");
        }

        if (MaximumEntryBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumEntryBytes),
                "The maximum entry size must be greater than zero.");
        }

        if (MaximumTotalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumTotalBytes),
                "The maximum total size must be greater than zero.");
        }
    }
}
