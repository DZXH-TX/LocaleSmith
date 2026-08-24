using LocaleSmith.App.Services;

namespace LocaleSmith.App.Tests;

public sealed class ApplicationStorageScopeTests
{
    [Fact]
    public void ProductionPackageUsesProductionStateAndCredentialNamespace()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "storage-scope-root");

        var scope = ApplicationStorageScope.Resolve(
            localAppData,
            ApplicationStorageScope.ProductionPackageFamilyName);

        Assert.True(scope.IsProduction);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(localAppData), ApplicationStorageScope.ProductionDirectoryName),
            scope.AppDataRoot);
        Assert.Equal(
            ApplicationStorageScope.ProductionCredentialTargetPrefix,
            scope.CredentialTargetPrefix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CRTech.LocaleSmith")]
    [InlineData("CRTech.LocaleSmith_otherpublisher")]
    [InlineData("LocaleSmith.Desktop")]
    [InlineData("CRTech.LocaleSmith.Dev")]
    public void NonProductionProcessesUseIsolatedDevelopmentState(string? packageFamilyName)
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "storage-scope-root");

        var scope = ApplicationStorageScope.Resolve(localAppData, packageFamilyName);

        Assert.False(scope.IsProduction);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(localAppData), ApplicationStorageScope.DevelopmentDirectoryName),
            scope.AppDataRoot);
        Assert.Equal(
            ApplicationStorageScope.DevelopmentCredentialTargetPrefix,
            scope.CredentialTargetPrefix);
    }
}
