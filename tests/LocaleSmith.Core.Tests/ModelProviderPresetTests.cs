using LocaleSmith.Core.Models;

namespace LocaleSmith.Core.Tests;

public sealed class ModelProviderPresetTests
{
    [Fact]
    public void OmitTokenOptionIsAppendedWithoutChangingPersistedEnumValues()
    {
        Assert.Equal(0, (int)OpenAiTokenLimitParameter.MaxTokens);
        Assert.Equal(1, (int)OpenAiTokenLimitParameter.MaxCompletionTokens);
        Assert.Equal(2, (int)OpenAiTokenLimitParameter.Omit);
    }

    [Fact]
    public void NetworkPresetsShareOpenAiCompatibleProtocolAndKeepEditableDefaults()
    {
        var expectedIds = new[]
        {
            ModelProviderPresets.DeepSeekId,
            ModelProviderPresets.QwenId,
            ModelProviderPresets.XiaomiMimoId,
            ModelProviderPresets.MiniMaxId,
            ModelProviderPresets.DoubaoId,
            ModelProviderPresets.ZhipuGlmId,
            ModelProviderPresets.KimiId,
            ModelProviderPresets.OpenAiId,
            ModelProviderPresets.CustomId
        };

        Assert.Equal(expectedIds, ModelProviderPresets.All.Select(static preset => preset.Id));
        Assert.All(
            ModelProviderPresets.All,
            static preset => Assert.Equal(ModelProviderKind.OpenAiCompatible, preset.Protocol));
        Assert.Equal("https://api.deepseek.com/", ModelProviderPresets.DeepSeek.DefaultEndpoint?.AbsoluteUri);
        Assert.Equal("deepseek-v4-pro", ModelProviderPresets.DeepSeek.DefaultModelName);
        Assert.Equal("qwen-plus", ModelProviderPresets.Qwen.DefaultModelName);
        Assert.Equal("mimo-v2.5-pro", ModelProviderPresets.XiaomiMimo.DefaultModelName);
        Assert.Equal("MiniMax-M2.7", ModelProviderPresets.MiniMax.DefaultModelName);
        Assert.Equal("https://ark.cn-beijing.volces.com/api/v3", ModelProviderPresets.Doubao.DefaultEndpoint?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("doubao-seed-2-0-lite-260215", ModelProviderPresets.Doubao.DefaultModelName);
        Assert.Equal("glm-5.2", ModelProviderPresets.ZhipuGlm.DefaultModelName);
        Assert.Equal("kimi-k3", ModelProviderPresets.Kimi.DefaultModelName);
        Assert.Equal("gpt-5.6", ModelProviderPresets.OpenAi.DefaultModelName);
        Assert.Equal(OpenAiTokenLimitParameter.MaxTokens, ModelProviderPresets.Doubao.DefaultTokenLimitParameter);
        Assert.Equal(OpenAiTokenLimitParameter.MaxTokens, ModelProviderPresets.ZhipuGlm.DefaultTokenLimitParameter);
        Assert.Equal(OpenAiTokenLimitParameter.MaxCompletionTokens, ModelProviderPresets.Kimi.DefaultTokenLimitParameter);
        Assert.Equal(OpenAiTokenLimitParameter.MaxCompletionTokens, ModelProviderPresets.MiniMax.DefaultTokenLimitParameter);
        Assert.False(ModelProviderPresets.XiaomiMimo.SupportsCustomTemperature);
        Assert.False(ModelProviderPresets.Kimi.SupportsCustomTemperature);
        Assert.True(ModelProviderPresets.Kimi.RequiresReasoningContentReplay);
        Assert.All(
            ModelProviderPresets.All.Where(static preset => preset.Id is not ModelProviderPresets.KimiId),
            static preset => Assert.False(preset.RequiresReasoningContentReplay));
        Assert.All(
            ModelProviderPresets.All.Where(static preset =>
                preset.Id is not ModelProviderPresets.XiaomiMimoId and not ModelProviderPresets.KimiId),
            static preset => Assert.True(preset.SupportsCustomTemperature));

        var kimiSource = new ModelSource(
            "kimi",
            "Kimi",
            ModelProviderKind.OpenAiCompatible,
            ModelProviderPresets.Kimi.DefaultEndpoint!,
            ModelProviderPresets.Kimi.DefaultModelName!,
            presetId: ModelProviderPresets.KimiId);
        Assert.True(kimiSource.RequiresReasoningContentReplay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("legacy-provider")]
    public void MissingOrUnknownLegacyPresetFallsBackToCustom(string? presetId)
    {
        var preset = ModelProviderPresets.ResolveOrCustom(presetId);

        Assert.Equal(ModelProviderPresets.CustomId, preset.Id);
        Assert.True(preset.IsCustom);
        Assert.Null(preset.DefaultEndpoint);
        Assert.Null(preset.DefaultModelName);
        Assert.Equal(OpenAiTokenLimitParameter.MaxTokens, preset.DefaultTokenLimitParameter);
    }

    [Fact]
    public void ModelSourceRejectsUnknownPersistedTokenParameter()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ModelSource(
            "invalid-token-parameter",
            "Invalid",
            ModelProviderKind.OpenAiCompatible,
            new Uri("https://models.example.test/v1"),
            "model",
            presetId: ModelProviderPresets.CustomId,
            tokenLimitParameter: (OpenAiTokenLimitParameter)999));

        Assert.Equal("tokenLimitParameter", exception.ParamName);
    }
}
