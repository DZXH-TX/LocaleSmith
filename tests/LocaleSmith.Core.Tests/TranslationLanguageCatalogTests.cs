using LocaleSmith.Core.Services;

namespace LocaleSmith.Core.Tests;

public sealed class TranslationLanguageCatalogTests
{
    [Fact]
    public void SupportedLanguagesHaveStableOrderAndMetadata()
    {
        Assert.Collection(
            TranslationLanguageCatalog.SupportedLanguages,
            language => AssertLanguage(language, "zh_CN", "zh_cn", "Chinese (Simplified)", "简体中文"),
            language => AssertLanguage(language, "en_US", "en_us", "English (United States)", "English (United States)"),
            language => AssertLanguage(language, "ja_JP", "ja_jp", "Japanese", "日本語"),
            language => AssertLanguage(language, "fr_FR", "fr_fr", "French", "Français"),
            language => AssertLanguage(language, "ru_RU", "ru_ru", "Russian", "Русский"));
    }

    [Theory]
    [InlineData(" zh-cn ", "zh_CN")]
    [InlineData("EN-us", "en_US")]
    [InlineData("ja_jp", "ja_JP")]
    [InlineData("fr-FR", "fr_FR")]
    [InlineData("RU_ru", "ru_RU")]
    public void NormalizeLocaleAcceptsSeparatorAndCaseAliases(string input, string expected)
    {
        Assert.Equal(expected, TranslationLanguageCatalog.NormalizeLocale(input));
        Assert.True(TranslationLanguageCatalog.TryNormalizeLocale(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("de_DE")]
    [InlineData("zh")]
    [InlineData("../../zh_CN")]
    public void UnknownOrMalformedLocaleIsNotSupported(string? locale)
    {
        Assert.False(TranslationLanguageCatalog.TryGet(locale, out _));
        Assert.False(TranslationLanguageCatalog.TryNormalizeLocale(locale, out _));
    }

    [Fact]
    public void NormalizeLocaleReportsAllSupportedChoicesForUnknownLocale()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => TranslationLanguageCatalog.NormalizeLocale("de_DE"));

        Assert.Contains("de_DE", exception.Message, StringComparison.Ordinal);
        foreach (var language in TranslationLanguageCatalog.SupportedLanguages)
        {
            Assert.Contains(language.CanonicalLocale, exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("de-DE", "de_de")]
    [InlineData(" ES_es ", "es_es")]
    public void MinecraftLocaleTokenNormalizationAllowsUnsupportedSourceLanguages(
        string input,
        string expected)
    {
        Assert.Equal(expected, TranslationLanguageCatalog.NormalizeMinecraftLocaleToken(input));
        Assert.True(TranslationLanguageCatalog.TryNormalizeMinecraftLocaleToken(input, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.False(TranslationLanguageCatalog.TryNormalizeLocale(input, out _));
    }

    [Theory]
    [InlineData("../de_de")]
    [InlineData("de de")]
    [InlineData("de.de")]
    public void MinecraftLocaleTokenNormalizationRejectsUnsafeCharacters(string input)
    {
        Assert.False(TranslationLanguageCatalog.TryNormalizeMinecraftLocaleToken(input, out _));
        Assert.Throws<ArgumentException>(() =>
            TranslationLanguageCatalog.NormalizeMinecraftLocaleToken(input));
    }

    private static void AssertLanguage(
        LocaleSmith.Core.Models.TranslationLanguage language,
        string canonicalLocale,
        string minecraftLocale,
        string englishName,
        string nativeName)
    {
        Assert.Equal(canonicalLocale, language.CanonicalLocale);
        Assert.Equal(minecraftLocale, language.MinecraftLocale);
        Assert.Equal(englishName, language.EnglishName);
        Assert.Equal(nativeName, language.NativeName);
        Assert.False(string.IsNullOrWhiteSpace(language.PromptLanguageName));
        Assert.False(string.IsNullOrWhiteSpace(language.FormalPromptGuidance));
        Assert.False(string.IsNullOrWhiteSpace(language.InformalPromptGuidance));
    }
}
