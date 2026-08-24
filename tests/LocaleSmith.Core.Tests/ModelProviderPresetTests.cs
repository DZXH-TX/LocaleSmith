using LocaleSmith.Core.Models;

namespace LocaleSmith.Core.Tests;

public sealed class ModelProviderPresetTests
{
    [Fact]
    public void ModelSourceValidatesOptionalRequestBudgets()
    {
        var minimum = new ModelSource(
            "minimum",
            "Minimum",
            ModelProviderKind.Ollama,
            new Uri("http://127.0.0.1:11434"),
            "model",
            maxOutputTokens: ModelSource.MinimumMaxOutputTokens,
            maxSourceCharactersPerRequest: ModelSource.MinimumMaxSourceCharactersPerRequest);
        var maximum = new ModelSource(
            "maximum",
            "Maximum",
            ModelProviderKind.Ollama,
            new Uri("http://127.0.0.1:11434"),
            "model",
            maxOutputTokens: ModelSource.MaximumMaxOutputTokens,
            maxSourceCharactersPerRequest: ModelSource.MaximumMaxSourceCharactersPerRequest);

        Assert.Equal(ModelSource.MinimumMaxOutputTokens, minimum.MaxOutputTokens);
        Assert.Equal(ModelSource.MaximumMaxSourceCharactersPerRequest, maximum.MaxSourceCharactersPerRequest);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelSource(
            "too-small",
            "Too small",
            ModelProviderKind.Ollama,
            new Uri("http://127.0.0.1:11434"),
            "model",
            maxOutputTokens: ModelSource.MinimumMaxOutputTokens - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelSource(
            "too-large",
            "Too large",
            ModelProviderKind.Ollama,
            new Uri("http://127.0.0.1:11434"),
            "model",
            maxSourceCharactersPerRequest: ModelSource.MaximumMaxSourceCharactersPerRequest + 1));
    }

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
        Assert.All(
            new[]
            {
                ModelProviderPresets.DeepSeek,
                ModelProviderPresets.XiaomiMimo,
                ModelProviderPresets.MiniMax,
                ModelProviderPresets.ZhipuGlm,
                ModelProviderPresets.Kimi
            },
            static preset => Assert.True(preset.RequiresReasoningContentReplay));
        Assert.All(
            ModelProviderPresets.All.Where(static preset => preset.Id is
                not ModelProviderPresets.DeepSeekId and
                not ModelProviderPresets.XiaomiMimoId and
                not ModelProviderPresets.MiniMaxId and
                not ModelProviderPresets.ZhipuGlmId and
                not ModelProviderPresets.KimiId),
            static preset => Assert.False(preset.RequiresReasoningContentReplay));
        Assert.True(ModelProviderPresets.MiniMax.UsesReasoningDetailsReplay);
        Assert.All(
            ModelProviderPresets.All.Where(static preset => preset.Id is not ModelProviderPresets.MiniMaxId),
            static preset => Assert.False(preset.UsesReasoningDetailsReplay));
        Assert.All(
            new[] { ModelProviderPresets.DeepSeek, ModelProviderPresets.XiaomiMimo },
            static preset => Assert.True(preset.RequiresNonNullToolCallContent));
        Assert.All(
            ModelProviderPresets.All.Where(static preset => preset.Id is
                not ModelProviderPresets.DeepSeekId and
                not ModelProviderPresets.XiaomiMimoId),
            static preset => Assert.False(preset.RequiresNonNullToolCallContent));
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

    [Theory]
    [InlineData(ModelProviderPresets.DeepSeekId, "https://api.deepseek.com", ModelProviderPresets.DeepSeekId)]
    [InlineData(ModelProviderPresets.DeepSeekId, "https://api.deepseek.com/v1/chat/completions", ModelProviderPresets.DeepSeekId)]
    [InlineData(ModelProviderPresets.QwenId, "https://dashscope.aliyuncs.com/compatible-mode/v1", ModelProviderPresets.QwenId)]
    [InlineData(ModelProviderPresets.QwenId, "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", ModelProviderPresets.QwenId)]
    [InlineData(ModelProviderPresets.QwenId, "https://dashscope-us.aliyuncs.com/compatible-mode/v1", ModelProviderPresets.QwenId)]
    [InlineData(ModelProviderPresets.QwenId, "https://llm-example.cn-beijing.maas.aliyuncs.com/compatible-mode/v1", ModelProviderPresets.QwenId)]
    [InlineData(ModelProviderPresets.QwenId, "https://trial.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1", ModelProviderPresets.QwenId)]
    [InlineData(ModelProviderPresets.QwenId, "https://token-plan.us-east-1.maas.aliyuncs.com/compatible-mode/v1", ModelProviderPresets.QwenId)]
    [InlineData(ModelProviderPresets.XiaomiMimoId, "https://api.xiaomimimo.com/v1", ModelProviderPresets.XiaomiMimoId)]
    [InlineData(ModelProviderPresets.XiaomiMimoId, "https://token-plan-cn.xiaomimimo.com/v1", ModelProviderPresets.XiaomiMimoId)]
    [InlineData(ModelProviderPresets.XiaomiMimoId, "https://token-plan-sgp.xiaomimimo.com/v1", ModelProviderPresets.XiaomiMimoId)]
    [InlineData(ModelProviderPresets.XiaomiMimoId, "https://token-plan-ams.xiaomimimo.com/v1", ModelProviderPresets.XiaomiMimoId)]
    [InlineData(ModelProviderPresets.KimiId, "https://api.moonshot.ai/v1", ModelProviderPresets.KimiId)]
    public void EffectivePresetKeepsOnlyExplicitOfficialEndpointShapes(
        string presetId,
        string endpoint,
        string expectedPresetId)
    {
        var effective = ModelProviderPresets.ResolveEffective(
            ModelProviderKind.OpenAiCompatible,
            presetId,
            new Uri(endpoint));

        Assert.Equal(expectedPresetId, effective.Id);
    }

    [Theory]
    [InlineData(ModelProviderPresets.DeepSeekId, "http://api.deepseek.com/v1")]
    [InlineData(ModelProviderPresets.DeepSeekId, "https://api.deepseek.com.evil.test/v1")]
    [InlineData(ModelProviderPresets.DeepSeekId, "https://deepseek.proxy.test/v1")]
    [InlineData(ModelProviderPresets.QwenId, "https://workspace.invalid-region.maas.aliyuncs.com/compatible-mode/v1")]
    [InlineData(ModelProviderPresets.QwenId, "https://extra.workspace.cn-beijing.maas.aliyuncs.com/compatible-mode/v1")]
    [InlineData(ModelProviderPresets.QwenId, "https://workspace.cn-beijing.maas.aliyuncs.com.evil.test/compatible-mode/v1")]
    [InlineData(ModelProviderPresets.QwenId, "https://example.aliyuncs.com/compatible-mode/v1")]
    [InlineData(ModelProviderPresets.QwenId, "https://dashscope.aliyuncs.com/api/v1")]
    [InlineData(ModelProviderPresets.XiaomiMimoId, "https://private.xiaomimimo.com/v1")]
    [InlineData(ModelProviderPresets.XiaomiMimoId, "https://api.xiaomimimo.com.evil.test/v1")]
    [InlineData(ModelProviderPresets.KimiId, "https://gateway.example.test/v1")]
    public void EffectivePresetDemotesCustomOrDeceptiveEndpoints(string presetId, string endpoint)
    {
        var effective = ModelProviderPresets.ResolveEffective(
            ModelProviderKind.OpenAiCompatible,
            presetId,
            new Uri(endpoint));

        Assert.Equal(ModelProviderPresets.CustomId, effective.Id);
    }

    [Fact]
    public void EffectivePresetDoesNotInferProviderIdentityFromEndpointOrModelMetadata()
    {
        var effective = ModelProviderPresets.ResolveEffective(
            ModelProviderKind.OpenAiCompatible,
            ModelProviderPresets.CustomId,
            ModelProviderPresets.DeepSeek.DefaultEndpoint!);
        var runtime = new ModelSource(
            "custom-deepseek-model-name",
            "Custom gateway",
            ModelProviderKind.OpenAiCompatible,
            new Uri("https://gateway.example.test/v1"),
            "deepseek-v4-pro",
            presetId: ModelProviderPresets.DeepSeekId,
            tokenLimitParameter: OpenAiTokenLimitParameter.MaxCompletionTokens);

        Assert.Equal(ModelProviderPresets.CustomId, effective.Id);
        Assert.Equal(ModelProviderPresets.CustomId, runtime.PresetId);
        Assert.Equal(OpenAiTokenLimitParameter.MaxCompletionTokens, runtime.TokenLimitParameter);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://localhost:11434/openai/v1/")]
    [InlineData("http://[::1]:11434/v1/chat/completions")]
    public void CustomLoopbackRequiresExplicitOpenAiV1Route(string endpoint) =>
        Assert.True(ModelProviderPresets.IsSupportedCustomLoopbackEndpoint(new Uri(endpoint)));

    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://127.0.0.1:11434/api/chat")]
    [InlineData("https://127.0.0.1:11434/v1")]
    [InlineData("http://models.example.test/v1")]
    public void NonCustomOrProviderNativeLoopbackRoutesAreNotAcceptedAsPlaintextCustomOpenAi(string endpoint) =>
        Assert.False(ModelProviderPresets.IsSupportedCustomLoopbackEndpoint(new Uri(endpoint)));

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
