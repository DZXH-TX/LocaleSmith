using System.Text.Json.Serialization;

namespace JaxI18n.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<OpenAiTokenLimitParameter>))]
public enum OpenAiTokenLimitParameter
{
    [JsonStringEnumMemberName("max_tokens")]
    MaxTokens,

    [JsonStringEnumMemberName("max_completion_tokens")]
    MaxCompletionTokens
}

/// <summary>
/// Editable provider defaults. A preset selects protocol metadata only and never changes the HTTP adapter contract.
/// </summary>
public sealed record ModelProviderPreset(
    string Id,
    string DisplayName,
    ModelProviderKind Protocol,
    Uri? DefaultEndpoint,
    string? DefaultModelName,
    Uri? DocumentationUri,
    OpenAiTokenLimitParameter DefaultTokenLimitParameter,
    bool SupportsCustomTemperature = true,
    bool RequiresReasoningContentReplay = false,
    bool IsCustom = false);

public static class ModelProviderPresets
{
    public const string DeepSeekId = "deepseek";
    public const string QwenId = "qwen";
    public const string XiaomiMimoId = "xiaomi-mimo";
    public const string MiniMaxId = "minimax";
    public const string DoubaoId = "doubao";
    public const string ZhipuGlmId = "zhipu-glm";
    public const string KimiId = "kimi";
    public const string OpenAiId = "openai";
    public const string CustomId = "custom";

    public static ModelProviderPreset DeepSeek { get; } = new(
        DeepSeekId,
        "DeepSeek",
        ModelProviderKind.OpenAiCompatible,
        new Uri("https://api.deepseek.com"),
        "deepseek-v4-pro",
        new Uri("https://api-docs.deepseek.com/api/create-chat-completion"),
        OpenAiTokenLimitParameter.MaxTokens);

    public static ModelProviderPreset Qwen { get; } = new(
        QwenId,
        "Qwen / Alibaba Cloud Model Studio",
        ModelProviderKind.OpenAiCompatible,
        new Uri("https://dashscope.aliyuncs.com/compatible-mode/v1"),
        "qwen-plus",
        new Uri("https://help.aliyun.com/en/model-studio/base-url"),
        OpenAiTokenLimitParameter.MaxTokens);

    public static ModelProviderPreset XiaomiMimo { get; } = new(
        XiaomiMimoId,
        "Xiaomi MiMo",
        ModelProviderKind.OpenAiCompatible,
        new Uri("https://api.xiaomimimo.com/v1"),
        "mimo-v2.5-pro",
        new Uri("https://mimo.mi.com/docs/api/chat/openai-api"),
        OpenAiTokenLimitParameter.MaxCompletionTokens,
        SupportsCustomTemperature: false);

    public static ModelProviderPreset MiniMax { get; } = new(
        MiniMaxId,
        "MiniMax",
        ModelProviderKind.OpenAiCompatible,
        new Uri("https://api.minimax.io/v1"),
        "MiniMax-M3",
        new Uri("https://platform.minimax.io/docs/api-reference/text-openai-api"),
        OpenAiTokenLimitParameter.MaxCompletionTokens);

    public static ModelProviderPreset Doubao { get; } = new(
        DoubaoId,
        "Doubao / 火山方舟",
        ModelProviderKind.OpenAiCompatible,
        new Uri("https://ark.cn-beijing.volces.com/api/v3"),
        "doubao-seed-2-0-lite-260215",
        new Uri("https://api.volcengine.com/api-docs/view?action=ChatCompletions&serviceCode=ark&version=2024-01-01"),
        OpenAiTokenLimitParameter.MaxTokens);

    public static ModelProviderPreset ZhipuGlm { get; } = new(
        ZhipuGlmId,
        "Zhipu GLM / 智谱开放平台",
        ModelProviderKind.OpenAiCompatible,
        new Uri("https://open.bigmodel.cn/api/paas/v4"),
        "glm-5.2",
        new Uri("https://docs.bigmodel.cn/cn/guide/develop/openai/introduction"),
        OpenAiTokenLimitParameter.MaxTokens);

    public static ModelProviderPreset Kimi { get; } = new(
        KimiId,
        "Kimi / Moonshot AI",
        ModelProviderKind.OpenAiCompatible,
        new Uri("https://api.moonshot.cn/v1"),
        "kimi-k3",
        new Uri("https://platform.kimi.com/docs/api/chat"),
        OpenAiTokenLimitParameter.MaxCompletionTokens,
        SupportsCustomTemperature: false,
        RequiresReasoningContentReplay: true);

    public static ModelProviderPreset OpenAi { get; } = new(
        OpenAiId,
        "OpenAI",
        ModelProviderKind.OpenAiCompatible,
        new Uri("https://api.openai.com/v1"),
        "gpt-5.6",
        new Uri("https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create"),
        OpenAiTokenLimitParameter.MaxCompletionTokens);

    public static ModelProviderPreset Custom { get; } = new(
        CustomId,
        "Custom OpenAI-compatible",
        ModelProviderKind.OpenAiCompatible,
        null,
        null,
        null,
        OpenAiTokenLimitParameter.MaxTokens,
        IsCustom: true);

    public static IReadOnlyList<ModelProviderPreset> All { get; } = Array.AsReadOnly(
        new[] { DeepSeek, Qwen, XiaomiMimo, MiniMax, Doubao, ZhipuGlm, Kimi, OpenAi, Custom });

    public static bool TryGet(string? id, out ModelProviderPreset preset)
    {
        preset = All.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Custom;
        return !string.IsNullOrWhiteSpace(id) &&
            string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Maps absent or unknown legacy metadata to Custom without changing endpoint or model fields.</summary>
    public static ModelProviderPreset ResolveOrCustom(string? id) => TryGet(id, out var preset) ? preset : Custom;
}
