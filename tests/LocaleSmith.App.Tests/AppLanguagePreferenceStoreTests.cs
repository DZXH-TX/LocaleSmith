using LocaleSmith.App.Services;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Tests;

public sealed class AppLanguagePreferenceStoreTests
{
    [Fact]
    public void MissingPreferenceUsesDefaultLanguage()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocaleSmith.Tests", Guid.NewGuid().ToString("N"));

        Assert.Equal(
            AppDisplayLanguages.DefaultLanguage,
            AppLanguagePreferenceStore.LoadOrDefault(root));
    }

    [Fact]
    public void SaveRoundTripsCanonicalLanguage()
    {
        var root = CreateTestRoot();
        try
        {
            AppLanguagePreferenceStore.Save(root, "EN-us");

            Assert.Equal(
                AppDisplayLanguages.EnglishUnitedStates,
                AppLanguagePreferenceStore.LoadOrDefault(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnsupportedPreferenceCannotBeSaved()
    {
        var root = CreateTestRoot();
        try
        {
            Assert.Throws<ArgumentException>(() =>
                AppLanguagePreferenceStore.Save(root, "fr-FR"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocaleSmith.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
