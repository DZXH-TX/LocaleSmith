using LocaleSmith.App.Converters;

namespace LocaleSmith.App.Tests;

public sealed class TranslationLogViewModeDisplayConverterTests
{
    [Theory]
    [InlineData("Debug", "调试")]
    [InlineData("AllLevels", "全部级别")]
    public void UsesLocalizedViewLabels(string value, string expected)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LogViewModeDebug"] = "调试",
            ["LogViewModeAllLevels"] = "全部级别"
        };
        var converter = new TranslationLogViewModeDisplayConverter(labels.GetValueOrDefault);

        Assert.Equal(expected, converter.Convert(value, typeof(string), string.Empty, "zh-CN"));
    }

    [Fact]
    public void UnknownValueFallsBackToItsDisplayText()
    {
        var converter = new TranslationLogViewModeDisplayConverter(_ => null);

        Assert.Equal("FutureMode", converter.Convert("FutureMode", typeof(string), string.Empty, "en-US"));
    }
}
