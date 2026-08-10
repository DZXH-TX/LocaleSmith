using LocaleSmith.App.Services;

namespace LocaleSmith.App.Tests;

public sealed class CliSandboxDirectoryTests
{
    [Fact]
    public void CreateUnderAppDataRootCreatesPrivateNonReparseDirectory()
    {
        var root = Directory.CreateTempSubdirectory("localesmith-cli-sandbox-");
        try
        {
            var appDataRoot = Path.Combine(root.FullName, "LocaleSmith");

            var sandbox = CliSandboxDirectory.CreateUnderAppDataRoot(appDataRoot);

            Assert.Equal(Path.Combine(appDataRoot, "CliSandbox"), sandbox);
            Assert.True(Directory.Exists(sandbox));
            Assert.False((File.GetAttributes(sandbox) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(sandbox, CliSandboxDirectory.ValidateExisting(appDataRoot, sandbox));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateUnderAppDataRootRejectsReparsePointAncestor()
    {
        var root = Directory.CreateTempSubdirectory("localesmith-cli-sandbox-root-");
        var external = Directory.CreateTempSubdirectory("localesmith-cli-sandbox-external-");
        var appDataLink = Path.Combine(root.FullName, "LocaleSmith");
        try
        {
            try
            {
                _ = Directory.CreateSymbolicLink(appDataLink, external.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Symbolic-link creation can be disabled by local Windows policy.
                return;
            }

            Assert.Throws<InvalidDataException>(
                () => CliSandboxDirectory.CreateUnderAppDataRoot(appDataLink));
            Assert.False(Directory.Exists(Path.Combine(external.FullName, "CliSandbox")));
        }
        finally
        {
            if (Directory.Exists(appDataLink))
            {
                Directory.Delete(appDataLink);
            }

            root.Delete(recursive: true);
            external.Delete(recursive: true);
        }
    }

    [Fact]
    public void ValidateExistingRejectsSandboxReplacedByReparsePoint()
    {
        var root = Directory.CreateTempSubdirectory("localesmith-cli-sandbox-replace-");
        var external = Directory.CreateTempSubdirectory("localesmith-cli-sandbox-target-");
        var appDataRoot = Path.Combine(root.FullName, "LocaleSmith");
        var sandbox = Path.Combine(appDataRoot, "CliSandbox");
        try
        {
            Directory.CreateDirectory(appDataRoot);
            try
            {
                _ = Directory.CreateSymbolicLink(sandbox, external.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Symbolic-link creation can be disabled by local Windows policy.
                return;
            }

            Assert.Throws<InvalidDataException>(
                () => CliSandboxDirectory.ValidateExisting(appDataRoot, sandbox));
        }
        finally
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox);
            }

            root.Delete(recursive: true);
            external.Delete(recursive: true);
        }
    }
}
