using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.App.Tests;

public sealed class ModelOptionSelectionMapTests
{
    [Theory]
    [InlineData(ModelProviderPresets.DeepSeekId)]
    [InlineData(ModelProviderPresets.XiaomiMimoId)]
    [InlineData(ModelProviderPresets.ZhipuGlmId)]
    public void PresetSelectionResolvesToCanonicalComboBoxItem(string presetId)
    {
        Assert.True(ModelProviderPresets.TryGet(presetId, out var selected));
        var equivalentItem = selected with { DisplayName = $"{selected.DisplayName} copy" };

        Assert.True(ModelOptionSelectionMap.TryResolvePreset(
            equivalentItem,
            ModelProviderPresets.All,
            out var resolved));
        Assert.Same(selected, resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("deepseek")]
    [InlineData(42)]
    public void InvalidPresetSelectionIsRejected(object? selectedItem) =>
        Assert.False(ModelOptionSelectionMap.TryResolvePreset(
            selectedItem,
            ModelProviderPresets.All,
            out _));

    [Fact]
    public void PresetOutsideItemsSourceIsRejected()
    {
        var unknown = ModelProviderPresets.Custom with { Id = "unknown" };

        Assert.False(ModelOptionSelectionMap.TryResolvePreset(
            unknown,
            ModelProviderPresets.All,
            out _));
    }

    [Theory]
    [InlineData(OpenAiTokenLimitParameter.Omit)]
    [InlineData(OpenAiTokenLimitParameter.MaxTokens)]
    [InlineData(OpenAiTokenLimitParameter.MaxCompletionTokens)]
    public void TokenSelectionResolvesToCanonicalComboBoxItem(OpenAiTokenLimitParameter value)
    {
        IReadOnlyList<TokenLimitParameterOption> options =
        [
            new(OpenAiTokenLimitParameter.Omit, "由服务端决定（不发送）"),
            new(OpenAiTokenLimitParameter.MaxTokens, "max_tokens"),
            new(OpenAiTokenLimitParameter.MaxCompletionTokens, "max_completion_tokens")
        ];

        Assert.True(ModelOptionSelectionMap.TryResolveTokenLimitParameter(
            new TokenLimitParameterOption(value, "copy"),
            options,
            out var resolved));
        Assert.Same(options.Single(option => option.Value == value), resolved);
    }

    [Fact]
    public void InvalidTokenSelectionIsRejected()
    {
        IReadOnlyList<TokenLimitParameterOption> options =
        [new(OpenAiTokenLimitParameter.Omit, "由服务端决定（不发送）")];

        Assert.False(ModelOptionSelectionMap.TryResolveTokenLimitParameter(
            new TokenLimitParameterOption(OpenAiTokenLimitParameter.MaxTokens, "max_tokens"),
            options,
            out _));
    }
}
