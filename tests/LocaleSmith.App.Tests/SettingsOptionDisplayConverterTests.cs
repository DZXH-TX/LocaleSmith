using LocaleSmith.App.Converters;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Tests;

public sealed class SettingsOptionDisplayConverterTests
{
    [Theory]
    [InlineData("zh-CN", "LanguageOptionZhCn")]
    [InlineData("en-US", "LanguageOptionEnUs")]
    [InlineData("ja-JP", "LanguageOptionJaJp")]
    [InlineData("fr-FR", "LanguageOptionFrFr")]
    [InlineData("ru-RU", "LanguageOptionRuRu")]
    public void UsesLocalizedLanguageLabels(string value, string resourceKey)
    {
        var expected = $"localized:{resourceKey}";
        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { [resourceKey] = expected };
        var converter = new SettingsOptionDisplayConverter(labels.GetValueOrDefault);

        Assert.Equal(expected, converter.Convert(value, typeof(string), string.Empty, "zh-CN"));
    }

    [Theory]
    [InlineData(AppThemePreference.System, "跟随系统")]
    [InlineData(AppThemePreference.Light, "浅色")]
    [InlineData(AppThemePreference.Dark, "深色")]
    public void UsesLocalizedThemeLabels(AppThemePreference value, string expected)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ThemeOptionSystem"] = "跟随系统",
            ["ThemeOptionLight"] = "浅色",
            ["ThemeOptionDark"] = "深色"
        };
        var converter = new SettingsOptionDisplayConverter(labels.GetValueOrDefault);

        Assert.Equal(expected, converter.Convert(value, typeof(string), string.Empty, "zh-CN"));
    }

    [Fact]
    public void UnknownValueFallsBackToItsDisplayText()
    {
        var converter = new SettingsOptionDisplayConverter(_ => null);

        Assert.Equal("FutureOption", converter.Convert("FutureOption", typeof(string), string.Empty, "en-US"));
    }
}
