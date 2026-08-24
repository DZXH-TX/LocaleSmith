using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Services;

internal static class LegacyDefaultPathNormalizer
{
    private const string LegacyProductDirectoryName = "JaxI18n";
    private const string CurrentProductDirectoryName = "LocaleSmith";
    private const int LogDirectorySchemaVersion = 2;

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
            .Select(NormalizeModelSourcePreset)
            .ToArray();
        var normalizeModelPresets = !configuration.ModelSources.SequenceEqual(normalizedModelSources);
        changed = migrateWorkspace || migrateSandbox || initializeLogDirectory || upgradeSchema || normalizeModelPresets;
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
            ModelSources = normalizeModelPresets
                ? normalizedModelSources
                : configuration.ModelSources
        };
    }

    internal static ModelSourceProfile NormalizeModelSourcePreset(ModelSourceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var configuredPreset = ModelProviderPresets.ResolveOrCustom(profile.PresetId);
        var endpointIsValid = Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var endpoint);
        var effectivePreset = endpointIsValid
            ? ModelProviderPresets.ResolveEffective(profile.Provider, configuredPreset.Id, endpoint!)
            : ModelProviderPresets.Custom;
        var normalizedPresetId = profile.Provider == ModelProviderKind.OpenAiCompatible
            ? effectivePreset.Id
            : ModelProviderPresets.CustomId;
        var normalizedTokenParameter = profile.TokenLimitParameter;
        if (profile.Provider == ModelProviderKind.OpenAiCompatible &&
            normalizedTokenParameter is null &&
            !string.Equals(configuredPreset.Id, normalizedPresetId, StringComparison.Ordinal))
        {
            normalizedTokenParameter = configuredPreset.DefaultTokenLimitParameter;
        }

        return string.Equals(profile.PresetId, normalizedPresetId, StringComparison.Ordinal) &&
            profile.TokenLimitParameter == normalizedTokenParameter
            ? profile
            : profile with
            {
                PresetId = normalizedPresetId,
                TokenLimitParameter = normalizedTokenParameter
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
