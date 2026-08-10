using System.Diagnostics;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Infrastructure.Security;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Services;

/// <summary>
/// Imports essential pre-LocaleSmith configuration and credentials without deleting legacy state.
/// Translation memory is promoted lazily by <see cref="FileTranslationMemoryStore"/>; historical
/// logs remain in the legacy root so startup work is bounded.
/// </summary>
public sealed class LegacyAppDataMigrator
{
    public const string CurrentConfigurationFileName = "settings.localesmithcfg";
    public const string CurrentConfigurationPurpose = "LocaleSmith.ApplicationSettings.v1";
    public const string LegacyConfigurationFileName = "settings.jaxcfg";
    public const string LegacyConfigurationPurpose = "JaxI18n.ApplicationSettings.v1";
    public const string LegacyAssociatedDataNamespace = "JaxI18n.Config";

    private readonly string _legacyRoot;
    private readonly string _currentRoot;
    private readonly ISecretStore _legacySecretStore;
    private readonly ISecretStore _currentSecretStore;

    public LegacyAppDataMigrator(
        string legacyRoot,
        string currentRoot,
        ISecretStore legacySecretStore,
        ISecretStore currentSecretStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRoot);
        _legacyRoot = Path.GetFullPath(legacyRoot);
        _currentRoot = Path.GetFullPath(currentRoot);
        if (PathsOverlap(_legacyRoot, _currentRoot))
        {
            throw new ArgumentException("Legacy and current application-data roots must be separate and non-nested.");
        }

        RejectReparsePoint(_legacyRoot);
        RejectReparsePoint(_currentRoot);

        _legacySecretStore = legacySecretStore ?? throw new ArgumentNullException(nameof(legacySecretStore));
        _currentSecretStore = currentSecretStore ?? throw new ArgumentNullException(nameof(currentSecretStore));
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await MigrateConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsNonFatalLegacyFailure(exception))
        {
            // Legacy state is optional. A stale master key, corrupt envelope, inaccessible file,
            // or malformed credential reference must never prevent a clean LocaleSmith startup.
            Debug.WriteLine($"Legacy application-data migration was skipped: {exception}");
        }
    }

    private async Task MigrateConfigurationAsync(CancellationToken cancellationToken)
    {
        var legacyPath = Path.Combine(_legacyRoot, LegacyConfigurationFileName);
        var currentPath = Path.Combine(_currentRoot, CurrentConfigurationFileName);
        if (File.Exists(currentPath) || !File.Exists(legacyPath))
        {
            return;
        }

        var legacyMasterKeys = new CredentialManagerMasterKeyStore(_legacySecretStore);
        var currentMasterKeys = new CredentialManagerMasterKeyStore(_currentSecretStore);
        using var legacyConfiguration = new EncryptedJsonConfigurationStore<AppConfiguration>(
            legacyPath,
            LegacyConfigurationPurpose,
            legacyMasterKeys,
            associatedDataNamespace: LegacyAssociatedDataNamespace);
        using var currentConfiguration = new EncryptedJsonConfigurationStore<AppConfiguration>(
            currentPath,
            CurrentConfigurationPurpose,
            currentMasterKeys);

        var configuration = await legacyConfiguration.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (configuration is null)
        {
            return;
        }

        configuration = LegacyDefaultPathNormalizer.Normalize(configuration, out _, _currentRoot);

        foreach (var reference in configuration.ModelSources
                     .Select(static profile => profile.CredentialReference)
                     .Where(static reference => !string.IsNullOrWhiteSpace(reference))
                     .Distinct(StringComparer.Ordinal))
        {
            await MigrateSecretAsync(reference!, cancellationToken).ConfigureAwait(false);
        }

        await currentConfiguration.SaveIfAbsentAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    private async Task MigrateSecretAsync(string reference, CancellationToken cancellationToken)
    {
        var migratingStore = new MigratingSecretStore(_currentSecretStore, _legacySecretStore);
        using var migrated = await migratingStore
            .ResolveAsync(reference, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool PathsOverlap(string first, string second)
    {
        var normalizedFirst = first.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedSecond = second.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedFirst.StartsWith(normalizedSecond, StringComparison.OrdinalIgnoreCase)
            || normalizedSecond.StartsWith(normalizedFirst, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparsePoint(string path)
    {
        if (Directory.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("An application-data migration root cannot be a reparse point.");
        }
    }

    private static bool IsNonFatalLegacyFailure(Exception exception) =>
        exception is not OperationCanceledException
        and not OutOfMemoryException;
}
