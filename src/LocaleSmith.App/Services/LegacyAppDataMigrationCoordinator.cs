using System.Diagnostics;
using LocaleSmith.Core.Abstractions;

namespace LocaleSmith.App.Services;

/// <summary>
/// Runs optional legacy-state migration without letting discovery or one invalid root prevent
/// the application from starting. Successfully validated roots are returned for lazy, read-only
/// translation-memory lookup.
/// </summary>
internal static class LegacyAppDataMigrationCoordinator
{
    public static Task<IReadOnlyList<string>> MigrateAsync(
        string currentRoot,
        ISecretStore legacySecretStore,
        ISecretStore currentSecretStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRoot);
        ArgumentNullException.ThrowIfNull(legacySecretStore);
        ArgumentNullException.ThrowIfNull(currentSecretStore);

        return RunBestEffortAsync(
            static () =>
            {
                var unredirectedLocalAppData = LegacyAppDataLocator
                    .GetUnredirectedLocalApplicationDataPath();
                return LegacyAppDataLocator.FindLegacyRoots(unredirectedLocalAppData);
            },
            async (legacyRoot, token) =>
            {
                var migrator = new LegacyAppDataMigrator(
                    legacyRoot,
                    currentRoot,
                    legacySecretStore,
                    currentSecretStore);
                await migrator.MigrateAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    internal static async Task<IReadOnlyList<string>> RunBestEffortAsync(
        Func<IEnumerable<string>> discoverRoots,
        Func<string, CancellationToken, Task> migrateRootAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discoverRoots);
        ArgumentNullException.ThrowIfNull(migrateRootAsync);
        cancellationToken.ThrowIfCancellationRequested();

        string[] discoveredRoots;
        try
        {
            discoveredRoots = discoverRoots()?.ToArray() ?? [];
        }
        catch (Exception exception) when (IsNonFatalLegacyFailure(exception))
        {
            Debug.WriteLine($"Legacy application-data discovery was skipped: {exception}");
            return [];
        }

        var migratedRoots = new List<string>(discoveredRoots.Length);
        var uniqueRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var discoveredRoot in discoveredRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullRoot = Path.GetFullPath(discoveredRoot);
                if (!uniqueRoots.Add(fullRoot))
                {
                    continue;
                }

                await migrateRootAsync(fullRoot, cancellationToken).ConfigureAwait(false);
                migratedRoots.Add(fullRoot);
            }
            catch (Exception exception) when (IsNonFatalLegacyFailure(exception))
            {
                Debug.WriteLine($"Legacy application-data root was skipped: {exception}");
            }
        }

        return migratedRoots.ToArray();
    }

    private static bool IsNonFatalLegacyFailure(Exception exception) =>
        exception is not OperationCanceledException
        and not OutOfMemoryException;
}
