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

    [Theory]
    [InlineData("ZH-cn", "zh-CN")]
    [InlineData("EN-us", "en-US")]
    [InlineData("JA-jp", "ja-JP")]
    [InlineData("FR-fr", "fr-FR")]
    [InlineData("RU-ru", "ru-RU")]
    public void SaveRoundTripsCanonicalLanguage(string input, string expected)
    {
        var root = CreateTestRoot();
        try
        {
            AppLanguagePreferenceStore.Save(root, input);

            Assert.Equal(
                expected,
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
                AppLanguagePreferenceStore.Save(root, "de-DE"));
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
