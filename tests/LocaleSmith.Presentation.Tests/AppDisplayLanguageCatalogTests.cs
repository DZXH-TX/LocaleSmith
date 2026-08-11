using LocaleSmith.Presentation.Models;

namespace LocaleSmith.Presentation.Tests;

public sealed class AppDisplayLanguageCatalogTests
{
    [Fact]
    public void CatalogDeclaresTheFiveInitialDisplayLanguagesInUiOrder()
    {
        Assert.Equal(
            ["zh-CN", "en-US", "ja-JP", "fr-FR", "ru-RU"],
            AppDisplayLanguages.Supported);
        Assert.Equal(AppDisplayLanguages.Supported.Count, AppDisplayLanguages.Catalog.Count);
        Assert.All(AppDisplayLanguages.Catalog, option =>
        {
            Assert.False(string.IsNullOrWhiteSpace(option.ResourceKey));
            Assert.False(string.IsNullOrWhiteSpace(option.FallbackDisplayName));
        });
    }

    [Theory]
    [InlineData("ZH-cn", "zh-CN")]
    [InlineData("EN-us", "en-US")]
    [InlineData("JA-jp", "ja-JP")]
    [InlineData("FR-fr", "fr-FR")]
    [InlineData("RU-ru", "ru-RU")]
    public void SupportedTagsResolveToCanonicalCasing(string input, string expected)
    {
        Assert.Equal(expected, AppDisplayLanguages.ResolveSupported(input));
        Assert.Equal(expected, AppDisplayLanguages.ResolveOrDefault(input));
    }

    [Fact]
    public void UnsupportedTagIsRejectedByStrictResolution()
    {
        Assert.Throws<ArgumentException>(() => AppDisplayLanguages.ResolveSupported("de-DE"));
        Assert.Equal(
            AppDisplayLanguages.DefaultLanguage,
            AppDisplayLanguages.ResolveOrDefault("de-DE"));
    }
}
