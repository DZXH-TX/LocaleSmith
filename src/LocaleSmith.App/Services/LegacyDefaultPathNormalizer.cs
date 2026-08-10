using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Services;

internal static class LegacyDefaultPathNormalizer
{
    private const string LegacyProductDirectoryName = "JaxI18n";
    private const string CurrentProductDirectoryName = "LocaleSmith";
    private const int LogDirectorySchemaVersion = 2;
    private const int ProviderDefaultsSchemaVersion = 3;

    public static AppConfiguration Normalize(
        AppConfiguration configuration,
        out bool changed,
        string? currentAppDataRoot = null)
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
        var previousCurrentSandbox = Path.Combine(temporaryRoot, CurrentProductDirectoryName, "Sandbox");
        var currentSandbox = currentAppDataRoot is null
            ? AppConfiguration.GetDefaultSandboxPath()
            : Path.Combine(Path.GetFullPath(currentAppDataRoot), "CliSandbox");
        var currentLogDirectory = currentAppDataRoot is null
            ? AppConfiguration.GetDefaultLogDirectoryPath()
            : Path.Combine(Path.GetFullPath(currentAppDataRoot), "logs", "translations");

        var migrateWorkspace = legacyWorkspace is not null
            && PathsEqual(configuration.WorkspacePath, legacyWorkspace);
        var migrateSandbox = PathsEqual(configuration.SandboxPath, legacySandbox)
            || PathsEqual(configuration.SandboxPath, previousCurrentSandbox);
        var upgradeSchema = configuration.SchemaVersion < AppConfiguration.CurrentSchemaVersion;
        var initializeLogDirectory = configuration.SchemaVersion < LogDirectorySchemaVersion
            || string.IsNullOrWhiteSpace(configuration.LogDirectoryPath);
        var normalizedModelSources = configuration.ModelSources
            .Select(NormalizeLegacyProviderDefaults)
            .ToArray();
        var migrateModelDefaults = configuration.SchemaVersion < ProviderDefaultsSchemaVersion
            && !configuration.ModelSources.SequenceEqual(normalizedModelSources);
        changed = migrateWorkspace || migrateSandbox || initializeLogDirectory || upgradeSchema || migrateModelDefaults;
        if (!changed)
        {
            return configuration;
        }

        return configuration with
        {
            SchemaVersion = upgradeSchema
                ? AppConfiguration.CurrentSchemaVersion
                : configuration.SchemaVersion,
            WorkspacePath = migrateWorkspace ? currentWorkspace! : configuration.WorkspacePath,
            SandboxPath = migrateSandbox ? currentSandbox : configuration.SandboxPath,
            LogDirectoryPath = initializeLogDirectory
                ? currentLogDirectory
                : configuration.LogDirectoryPath,
            ModelSources = migrateModelDefaults
                ? normalizedModelSources
                : configuration.ModelSources
        };
    }

    private static ModelSourceProfile NormalizeLegacyProviderDefaults(ModelSourceProfile profile)
    {
        if (profile.Provider != LocaleSmith.Core.Models.ModelProviderKind.OpenAiCompatible ||
            !LocaleSmith.Core.Models.ModelProviderPresets.TryGet(profile.PresetId, out var preset) ||
            preset.IsCustom ||
            preset.DefaultEndpoint is null ||
            string.IsNullOrWhiteSpace(preset.DefaultModelName) ||
            !string.Equals(
                profile.Endpoint?.TrimEnd('/'),
                "http://127.0.0.1:11434",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(profile.ModelName?.Trim(), "llama3", StringComparison.OrdinalIgnoreCase))
        {
            return profile;
        }

        return profile with
        {
            Endpoint = preset.DefaultEndpoint.AbsoluteUri,
            ModelName = preset.DefaultModelName
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
