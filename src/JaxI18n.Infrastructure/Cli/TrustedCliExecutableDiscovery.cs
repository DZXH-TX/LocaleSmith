namespace JaxI18n.Infrastructure.Cli;

/// <summary>
/// Discovers the deliberately small set of executables that may be placed on
/// the application's initial CLI allowlist. User PATH entries are never trusted.
/// </summary>
public static class TrustedCliExecutableDiscovery
{
    public static IReadOnlyList<string> FindInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var programFiles = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            return [];
        }

        // Keep the default capability intentionally narrow. Additional tools must
        // be added through the dynamic allowlist after an explicit user decision.
        string[] relativeCandidates = [Path.Combine("dotnet", "dotnet.exe")];
        return relativeCandidates
            .Select(relative => Path.GetFullPath(Path.Combine(programFiles, relative)))
            .Where(path => IsTrustedRegularFile(path, programFiles))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsTrustedRegularFile(string candidate, string programFiles)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFiles));
            var path = Path.GetFullPath(candidate);
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var current = root;
            foreach (var segment in Path.GetRelativePath(root, path).Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }

            return File.Exists(path)
                && (File.GetAttributes(path) & FileAttributes.Directory) == 0;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return false;
        }
    }
}
