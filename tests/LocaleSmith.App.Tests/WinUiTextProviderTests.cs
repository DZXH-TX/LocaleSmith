using LocaleSmith.App.Services;

namespace LocaleSmith.App.Tests;

public sealed class WinUiTextProviderTests
{
    [Fact]
    public void UsesLocalizedMrtValueInsteadOfEnglishFallback()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["QueueStyleFormal"] = "正式翻译",
            ["QueueStyleInformal"] = "语气翻译"
        };
        var provider = new WinUiTextProvider(key => values.GetValueOrDefault(key));

        Assert.Equal("正式翻译", provider.GetText("QueueStyleFormal", "Formal translation"));
        Assert.Equal("语气翻译", provider.GetText("QueueStyleInformal", "Tone translation"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOrBlankMrtValueUsesFallback(string? localized)
    {
        var provider = new WinUiTextProvider(_ => localized);

        Assert.Equal("Fallback", provider.GetText("Missing", "Fallback"));
    }

    [Fact]
    public void FormatsTheLocalizedTemplate()
    {
        var provider = new WinUiTextProvider(_ => "已处理 {0} 个条目");

        Assert.Equal("已处理 3 个条目", provider.GetText("ProcessedCount", "Processed {0} entries", 3));
    }
}
