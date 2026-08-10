using System.Diagnostics.CodeAnalysis;
using LocaleSmith.App.Services;

namespace LocaleSmith.App.Tests;

public sealed class LegacyAppDataMigrationCoordinatorTests
{
    [Fact]
    public async Task DiscoveryFailureReturnsNoRootsAndDoesNotRunMigration()
    {
        var migrationCalled = false;

        var roots = await LegacyAppDataMigrationCoordinator.RunBestEffortAsync(
            static () => throw new IOException("simulated discovery failure"),
            (_, _) =>
            {
                migrationCalled = true;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Empty(roots);
        Assert.False(migrationCalled);
    }

    [Fact]
    public async Task InvalidAndFailingRootsDoNotPreventLaterRootMigration()
    {
        var root = Directory.CreateTempSubdirectory("localesmith-migration-coordinator-");
        try
        {
            var constructorFailureRoot = Path.Combine(root.FullName, "constructor-failure");
            var migrationFailureRoot = Path.Combine(root.FullName, "migration-failure");
            var successfulRoot = Path.Combine(root.FullName, "successful");
            var attemptedRoots = new List<string>();

            var roots = await LegacyAppDataMigrationCoordinator.RunBestEffortAsync(
                () => ["\0invalid-path", constructorFailureRoot, migrationFailureRoot, successfulRoot],
                (legacyRoot, _) =>
                {
                    attemptedRoots.Add(legacyRoot);
                    if (string.Equals(
                            legacyRoot,
                            Path.GetFullPath(constructorFailureRoot),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("simulated migrator construction failure");
                    }

                    if (string.Equals(
                            legacyRoot,
                            Path.GetFullPath(migrationFailureRoot),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("simulated migration failure");
                    }

                    return Task.CompletedTask;
                },
                TestContext.Current.CancellationToken);

            Assert.Equal(3, attemptedRoots.Count);
            Assert.Equal(Path.GetFullPath(successfulRoot), Assert.Single(roots));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MigrationCancellationPropagates()
    {
        var root = Path.Combine(Path.GetTempPath(), "localesmith-cancelled-migration");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LegacyAppDataMigrationCoordinator.RunBestEffortAsync(
                () => [root],
                static (_, _) => Task.FromException(
                    new OperationCanceledException("simulated cancellation")),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscoveryOutOfMemoryFailurePropagates()
    {
        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            LegacyAppDataMigrationCoordinator.RunBestEffortAsync(
                static () => throw CreateSimulatedOutOfMemoryException(
                    "simulated discovery exhaustion"),
                static (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MigrationOutOfMemoryFailurePropagates()
    {
        var root = Path.Combine(Path.GetTempPath(), "localesmith-exhausted-migration");

        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            LegacyAppDataMigrationCoordinator.RunBestEffortAsync(
                () => [root],
                static (_, _) => Task.FromException(
                    CreateSimulatedOutOfMemoryException("simulated migration exhaustion")),
                TestContext.Current.CancellationToken));
    }

    [SuppressMessage(
        "Usage",
        "CA2201:Do not raise reserved exception types",
        Justification = "The coordinator must prove that a real OutOfMemoryException is never swallowed.")]
    private static OutOfMemoryException CreateSimulatedOutOfMemoryException(string message) =>
        new(message);
}
