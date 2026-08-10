using LocaleSmith.App.Services;

namespace LocaleSmith.App.Tests;

public sealed class LegacyAppDataLocatorTests
{
    [Fact]
    public void FindLegacyRootsOrdersRegisteredMsixStateBeforeUnpackagedFallback()
    {
        var root = Directory.CreateTempSubdirectory("localesmith-legacy-locator-");
        try
        {
            const string firstPackageFamilyName = "JaxI18n.Desktop_apublisher";
            const string secondPackageFamilyName = "JaxI18n.Desktop_zpublisher";
            var secondMsixRoot = CreateMsixLegacyRoot(root.FullName, secondPackageFamilyName);
            var firstMsixRoot = CreateMsixLegacyRoot(root.FullName, firstPackageFamilyName);
            var unpackagedRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "JaxI18n")).FullName;
            _ = CreateMsixLegacyRoot(root.FullName, "JaxI18n.Desktop_unregistered");

            var roots = LegacyAppDataLocator.FindLegacyRoots(
                root.FullName,
                [secondPackageFamilyName, firstPackageFamilyName]);

            Assert.Equal([firstMsixRoot, secondMsixRoot, unpackagedRoot], roots);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void FindLegacyRootsUsesUnpackagedFallbackWhenNoLegacyPackageIsRegistered()
    {
        var root = Directory.CreateTempSubdirectory("localesmith-legacy-locator-fallback-");
        try
        {
            var unpackagedRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "JaxI18n")).FullName;

            var roots = LegacyAppDataLocator.FindLegacyRoots(root.FullName, []);

            Assert.Equal([unpackagedRoot], roots);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void FindLegacyRootsReturnsEmptyWhenNoLegacyStateExists()
    {
        var root = Directory.CreateTempSubdirectory("localesmith-legacy-locator-empty-");
        try
        {
            Assert.Empty(LegacyAppDataLocator.FindLegacyRoots(root.FullName, []));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void FindLegacyRootsRejectsPackageFamilyNamesThatCouldEscapePackagesRoot()
    {
        var root = Directory.CreateTempSubdirectory("localesmith-legacy-locator-boundary-");
        try
        {
            var fallback = Directory.CreateDirectory(Path.Combine(root.FullName, "JaxI18n")).FullName;
            var roots = LegacyAppDataLocator.FindLegacyRoots(
                root.FullName,
                ["JaxI18n.Desktop_..\\..\\escape", "Unrelated.Package_family"]);

            Assert.Equal([fallback], roots);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void FindLegacyRootsRejectsReparsePointPackageState()
    {
        var root = Directory.CreateTempSubdirectory("localesmith-legacy-locator-reparse-");
        var external = Directory.CreateTempSubdirectory("localesmith-legacy-locator-external-");
        const string packageFamilyName = "JaxI18n.Desktop_reparse";
        var packagesRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "Packages")).FullName;
        var packageLink = Path.Combine(packagesRoot, packageFamilyName);
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(external.FullName, "LocalCache", "Local", "JaxI18n"));
            try
            {
                _ = Directory.CreateSymbolicLink(packageLink, external.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Symbolic-link creation can be disabled by local Windows policy.
                return;
            }

            Assert.Empty(LegacyAppDataLocator.FindLegacyRoots(root.FullName, [packageFamilyName]));
        }
        finally
        {
            if (Directory.Exists(packageLink))
            {
                Directory.Delete(packageLink);
            }

            root.Delete(recursive: true);
            external.Delete(recursive: true);
        }
    }

    [Fact]
    public void UnredirectedLocalApplicationDataPathIsAbsoluteAndExists()
    {
        var path = LegacyAppDataLocator.GetUnredirectedLocalApplicationDataPath();

        Assert.True(Path.IsPathFullyQualified(path));
        Assert.True(Directory.Exists(path));
    }

    private static string CreateMsixLegacyRoot(string localAppDataRoot, string packageFamilyName)
    {
        return Directory.CreateDirectory(Path.Combine(
            localAppDataRoot,
            "Packages",
            packageFamilyName,
            "LocalCache",
            "Local",
            "JaxI18n")).FullName;
    }
}
