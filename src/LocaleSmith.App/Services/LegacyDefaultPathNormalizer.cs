using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Services;

internal static class LegacyDefaultPathNormalizer
{
    private const string LegacyProductDirectoryName = "JaxI18n";
    private const string CurrentProductDirectoryName = "LocaleSmith";

    public static AppConfiguration Normalize(AppConfiguration configuration, out bool changed)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var documentsRoot = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.MyDocuments);
        var legacyWorkspace = string.IsNullOrWhiteSpace(documentsRoot)
            ? null
            : Path.Combine(documentsRoot, LegacyProductDirectoryName);
        var currentWorkspace = string.IsNullOrWhiteSpace(documentsRoot)
            ? null
            : Path.Combine(documentsRoot, CurrentProductDirectoryName);
        var temporaryRoot = Path.GetTempPath();
        var legacySandbox = Path.Combine(temporaryRoot, LegacyProductDirectoryName, "Sandbox");
        var currentSandbox = Path.Combine(temporaryRoot, CurrentProductDirectoryName, "Sandbox");

        var migrateWorkspace = legacyWorkspace is not null
            && PathsEqual(configuration.WorkspacePath, legacyWorkspace);
        var migrateSandbox = PathsEqual(configuration.SandboxPath, legacySandbox);
        changed = migrateWorkspace || migrateSandbox;
        if (!changed)
        {
            return configuration;
        }

        return configuration with
        {
            WorkspacePath = migrateWorkspace ? currentWorkspace! : configuration.WorkspacePath,
            SandboxPath = migrateSandbox ? currentSandbox : configuration.SandboxPath
        };
    }

    private static bool PathsEqual(string candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedExpected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected));
        return string.Equals(normalizedCandidate, normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }
}
