using LocaleSmith.Core.Models;

namespace LocaleSmith.Presentation.Models;

public enum AppThemePreference
{
    System,
    Light,
    Dark
}

public sealed record ModelSourceProfile
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public ModelProviderKind Provider { get; init; }

    /// <summary>
    /// Stable editable-default preset metadata. Legacy profiles without this field deserialize as Custom.
    /// </summary>
    public string PresetId { get; init; } = ModelProviderPresets.CustomId;

    /// <summary>
    /// Optional for backward compatibility. Missing legacy values use the selected preset default,
    /// or <c>max_tokens</c> for Custom OpenAI-compatible sources.
    /// </summary>
    public OpenAiTokenLimitParameter? TokenLimitParameter { get; init; }

    public string Endpoint { get; init; } = string.Empty;

    public string ModelName { get; init; } = string.Empty;

    /// <summary>A Windows Credential Manager reference. This is never an API key value.</summary>
    public string? CredentialReference { get; init; }

    /// <summary>A non-secret, truncated SHA-256 fingerprint used only to identify a saved credential.</summary>
    public string? CredentialFingerprint { get; init; }
}

public sealed record AppConfiguration
{
    public const int CurrentSchemaVersion = 3;

    public static string GetDefaultLogDirectoryPath()
    {
        var localApplicationData = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The per-user application-data directory is unavailable.");
        }

        return Path.Combine(localApplicationData, "LocaleSmith", "logs", "translations");
    }

    public static string NormalizeLogDirectoryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("The log directory does not have a filesystem root.", nameof(path));
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A drive root cannot be used as the translation log directory.", nameof(path));
        }

        if (OperatingSystem.IsWindows() &&
            (fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
             new DriveInfo(root).DriveType == DriveType.Network))
        {
            throw new ArgumentException(
                "Translation logs require a local directory so an unavailable network share cannot stall diagnostics.",
                nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static string GetDefaultSandboxPath()
    {
        var localApplicationData = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The per-user application-data directory is unavailable.");
        }

        return Path.Combine(localApplicationData, "LocaleSmith", "CliSandbox");
    }

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool IsOnboardingComplete { get; init; }

    public string WorkspacePath { get; init; } = string.Empty;

    public string SandboxPath { get; init; } = GetDefaultSandboxPath();

    /// <summary>
    /// Directory containing per-translation diagnostic logs. Pre-schema-2 settings are upgraded
    /// at startup so migrations can choose the correct target-root default.
    /// </summary>
    public string LogDirectoryPath { get; init; } = GetDefaultLogDirectoryPath();

    public string Language { get; init; } = "zh-CN";

    public AppThemePreference Theme { get; init; } = AppThemePreference.System;

    /// <summary>
    /// Allows LocaleSmith's own short transitions to run when Windows UI animations are disabled.
    /// System and other application animation preferences are never changed.
    /// </summary>
    public bool ForceAppAnimations { get; init; }

    public string? SelectedModelSourceId { get; init; }

    public IReadOnlyList<ModelSourceProfile> ModelSources { get; init; } = [];
}

/// <summary>
/// The non-secret settings edited by the settings page. Model-source credentials and catalog state
/// are deliberately excluded so a stale page cannot overwrite a newer model-source transaction.
/// </summary>
public sealed record AppSettingsUpdate(
    string Language,
    AppThemePreference Theme,
    bool ForceAppAnimations,
    string WorkspacePath,
    string SandboxPath,
    string? LogDirectoryPath = null);

public sealed record OnboardingSubmission(
    string WorkspacePath,
    string SandboxPath,
    bool ConfigureOllama,
    Uri OllamaEndpoint,
    string OllamaModelName,
    string? NetworkPresetId = null,
    Uri? NetworkEndpoint = null,
    string? NetworkModelName = null,
    ReadOnlyMemory<char> NetworkApiKey = default,
    OpenAiTokenLimitParameter? NetworkTokenLimitParameter = null,
    string? LogDirectoryPath = null);

public sealed record ModelSourceDraft(
    string? Id,
    string DisplayName,
    ModelProviderKind Provider,
    Uri Endpoint,
    string ModelName,
    string? CredentialReference,
    string PresetId = ModelProviderPresets.CustomId,
    OpenAiTokenLimitParameter? TokenLimitParameter = null);

public sealed record ModelConnectionResult(bool IsSuccessful, string Message)
{
    public static ModelConnectionResult Success(string message) => new(true, message);

    public static ModelConnectionResult Failure(string message) => new(false, message);
}

public enum ShellSection
{
    Onboarding,
    Dashboard,
    Assistant,
    ModelSources,
    Logs,
    Settings
}
