namespace LocaleSmith.Archive;

internal static class ArchivePathSafety
{
    private static readonly HashSet<string> ReservedWindowsNames = new(
        new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        },
        StringComparer.OrdinalIgnoreCase);

    public static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Paths cannot contain a null character.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static string CombineArchivePath(string root, string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        string normalized = ValidateArchiveRelativePath(archivePath);
        string canonicalRoot = Canonicalize(root);
        string combined = Canonicalize(
            Path.Combine(canonicalRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path '{combined}' escapes the approved root '{canonicalRoot}'.");
        }

        return combined;
    }

    public static string ValidateArchiveRelativePath(string archivePath)
    {
        if (archivePath.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException("An archive entry contains a null character.");
        }

        string normalized = archivePath.Replace('\\', '/');
        bool isDirectory = normalized.EndsWith('/');
        string trimmed = isDirectory ? normalized[..^1] : normalized;
        if (trimmed.Length == 0 || normalized.StartsWith('/') || Path.IsPathRooted(normalized))
        {
            throw new InvalidDataException($"Unsafe archive path '{archivePath}'.");
        }

        string[] segments = trimmed.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Contains(':'))
            {
                throw new InvalidDataException($"Unsafe archive path '{archivePath}'.");
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                throw new InvalidDataException($"Archive path is unsafe under Windows normalization: '{archivePath}'.");
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException($"Archive path contains a character invalid on Windows: '{archivePath}'.");
            }

            string deviceStem = segment.Split('.')[0].TrimEnd(' ', '.');
            if (ReservedWindowsNames.Contains(deviceStem))
            {
                throw new InvalidDataException($"Archive path uses a reserved Windows device name: '{archivePath}'.");
            }
        }

        return isDirectory ? $"{string.Join('/', segments)}/" : string.Join('/', segments);
    }

    public static void EnsureChildPath(string root, string candidate)
    {
        string canonicalRoot = Canonicalize(root);
        string canonicalCandidate = Canonicalize(candidate);
        string prefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!canonicalCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path '{canonicalCandidate}' escapes the approved root '{canonicalRoot}'.");
        }
    }

    public static bool IsSameOrChildPath(string root, string candidate)
    {
        string canonicalRoot = Canonicalize(root);
        string canonicalCandidate = Canonicalize(candidate);
        return string.Equals(canonicalRoot, canonicalCandidate, StringComparison.OrdinalIgnoreCase) ||
            canonicalCandidate.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    public static void RejectReparsePointsInExistingDirectoryAncestry(string path)
    {
        var current = new DirectoryInfo(Canonicalize(path));
        while (current is not null)
        {
            current.Refresh();
            if (current.Exists &&
                ((current.Attributes & FileAttributes.ReparsePoint) != 0 || current.LinkTarget is not null))
            {
                throw new InvalidDataException(
                    $"Security-sensitive paths cannot traverse a symbolic link or reparse point: '{current.FullName}'.");
            }

            current = current.Parent;
        }
    }

    public static bool IsSymbolicLink(int externalAttributes)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        int unixMode = (externalAttributes >> 16) & 0xFFFF;
        return (unixMode & UnixFileTypeMask) == UnixSymbolicLink;
    }

    public static bool IsJarSignaturePath(string archivePath)
    {
        string normalized = archivePath.Replace('\\', '/');
        if (!normalized.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fileName = normalized["META-INF/".Length..];
        if (fileName.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        return fileName.EndsWith(".SF", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".EC", StringComparison.OrdinalIgnoreCase);
    }
}
