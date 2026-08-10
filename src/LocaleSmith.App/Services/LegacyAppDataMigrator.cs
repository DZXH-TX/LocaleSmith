using LocaleSmith.Core.Abstractions;
using LocaleSmith.Infrastructure.Security;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Services;

/// <summary>
/// Copies pre-LocaleSmith application state into the new storage namespace without deleting
/// the legacy state. The operation is idempotent so rollback to an older build remains possible.
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
        await MigrateConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await CopyDirectoryMissingFilesBestEffortAsync("translation-memory", cancellationToken).ConfigureAwait(false);
        await CopyDirectoryMissingFilesBestEffortAsync("logs", cancellationToken).ConfigureAwait(false);
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

        configuration = LegacyDefaultPathNormalizer.Normalize(configuration, out _);

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

    private async Task CopyDirectoryMissingFilesBestEffortAsync(
        string relativeDirectory,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.Combine(_legacyRoot, relativeDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        var destinationRoot = Path.Combine(_currentRoot, relativeDirectory);
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        IEnumerable<string> sourcePaths;
        try
        {
            sourcePaths = Directory.EnumerateFiles(sourceRoot, "*", enumeration).ToArray();
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                var destinationPath = ResolveContainedPath(destinationRoot, relativePath);
                if (File.Exists(destinationPath))
                {
                    continue;
                }

                var destinationDirectory = Path.GetDirectoryName(destinationPath)
                    ?? throw new InvalidOperationException("A migrated file has no parent directory.");
                EnsureNoReparsePoint(destinationRoot, destinationDirectory);
                Directory.CreateDirectory(destinationDirectory);
                EnsureNoReparsePoint(destinationRoot, destinationDirectory);
                var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.migrating";
                try
                {
                    await using (var source = new FileStream(
                                     sourcePath,
                                     FileMode.Open,
                                     FileAccess.Read,
                                     FileShare.ReadWrite | FileShare.Delete,
                                     16 * 1024,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var destination = new FileStream(
                                     temporaryPath,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     16 * 1024,
                                     FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    try
                    {
                        File.Move(temporaryPath, destinationPath, overwrite: false);
                    }
                    catch (IOException) when (File.Exists(destinationPath))
                    {
                        // A concurrent application instance completed the same migration first.
                    }
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
            catch (IOException)
            {
                // Non-critical caches and logs remain in the legacy root and are retried next launch.
            }
            catch (UnauthorizedAccessException)
            {
                // Non-critical caches and logs must not prevent secure configuration migration.
            }
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A legacy application-data path escaped its migration root.");
        }

        return candidate;
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

    private static void EnsureNoReparsePoint(string root, string candidateDirectory)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(candidateDirectory);
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                candidate.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A migration destination escaped its application-data root.");
        }

        for (var current = new DirectoryInfo(candidate);
             current is not null
             && (current.FullName.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(
                     current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                     root.TrimEnd(Path.DirectorySeparatorChar),
                     StringComparison.OrdinalIgnoreCase));
             current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("A migration destination ancestor cannot be a reparse point.");
            }
        }
    }
}
