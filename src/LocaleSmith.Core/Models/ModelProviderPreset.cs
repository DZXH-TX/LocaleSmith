using System.Text.Json.Serialization;

namespace LocaleSmith.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<OpenAiTokenLimitParameter>))]
public enum OpenAiTokenLimitParameter
{
    [JsonStringEnumMemberName("max_tokens")]
    MaxTokens,

    [JsonStringEnumMemberName("max_completion_tokens")]
    MaxCompletionTokens,

    /// <summary>
    /// Do not send a completion-token limit field. The provider or model applies its own default.
    /// </summary>
    [JsonStringEnumMemberName("omit")]
    Omit
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
    bool UsesReasoningDetailsReplay = false,
    bool RequiresNonNullToolCallContent = false,
    bool IsCustom = false);

public static class ModelProviderPresets
{
    private static readonly HashSet<string> QwenDashScopeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "dashscope.aliyuncs.com",
        "dashscope-intl.aliyuncs.com",
        "dashscope-us.aliyuncs.com"
    };

    private static readonly HashSet<string> QwenWorkspaceRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "cn-beijing",
        "ap-southeast-1",
        "ap-northeast-1",
        "eu-central-1",
        "us-east-1"
    };

    private static readonly HashSet<string> XiaomiMimoHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.xiaomimimo.com",
        "token-plan-cn.xiaomimimo.com",
        "token-plan-sgp.xiaomimimo.com",
        "token-plan-ams.xiaomimimo.com"
    };

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
        OpenAiTokenLimitParameter.MaxTokens,
        RequiresReasoningContentReplay: true,
        RequiresNonNullToolCallContent: true);

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
        SupportsCustomTemperature: false,
        RequiresReasoningContentReplay: true,
        RequiresNonNullToolCallContent: true);

    public static ModelProviderPreset MiniMax { get; } = new(
        MiniMaxId,
        "MiniMax",
        ModelProviderKind.OpenAiCompatible,
        new Uri("https://api.minimax.io/v1"),
        "MiniMax-M2.7",
        new Uri("https://platform.minimax.io/docs/api-reference/text-openai-api"),
        OpenAiTokenLimitParameter.MaxCompletionTokens,
        RequiresReasoningContentReplay: true,
        UsesReasoningDetailsReplay: true);

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
        OpenAiTokenLimitParameter.MaxTokens,
        RequiresReasoningContentReplay: true);

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

    /// <summary>
    /// Resolves persisted or edited preset metadata against the endpoint that will actually receive credentials.
    /// A named preset is provider identity metadata, so an arbitrary compatible proxy remains Custom even when it
    /// serves a model with a provider-like name.
    /// </summary>
    public static ModelProviderPreset ResolveEffective(
        ModelProviderKind provider,
        string? presetId,
        Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var requested = ResolveOrCustom(presetId);
        return provider == ModelProviderKind.OpenAiCompatible && IsOfficialEndpoint(requested, endpoint)
            ? requested
            : Custom;
    }

    /// <summary>
    /// Returns whether an HTTPS endpoint is one of the explicitly supported official OpenAI-compatible routes for
    /// a named preset. Host comparisons use the canonical IDN host and never substring matching.
    /// </summary>
    public static bool IsOfficialEndpoint(ModelProviderPreset preset, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (preset.IsCustom ||
            !endpoint.IsAbsoluteUri ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !endpoint.IsDefaultPort ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            return false;
        }

        var hostMatches = preset.Id switch
        {
            DeepSeekId => HostEquals(endpoint, "api.deepseek.com"),
            QwenId => IsOfficialQwenHost(endpoint.IdnHost),
            XiaomiMimoId => XiaomiMimoHosts.Contains(endpoint.IdnHost),
            MiniMaxId => HostEquals(endpoint, "api.minimax.io"),
            DoubaoId => HostEquals(endpoint, "ark.cn-beijing.volces.com"),
            ZhipuGlmId => HostEquals(endpoint, "open.bigmodel.cn"),
            KimiId => HostEquals(endpoint, "api.moonshot.cn") || HostEquals(endpoint, "api.moonshot.ai"),
            OpenAiId => HostEquals(endpoint, "api.openai.com"),
            _ => false
        };
        return hostMatches && HasOfficialOpenAiPath(preset.Id, endpoint.AbsolutePath);
    }

    /// <summary>
    /// Custom OpenAI-compatible loopback services may use plaintext HTTP, but the base URI must explicitly select
    /// their OpenAI-compatible v1 route rather than being mistaken for a provider-native endpoint.
    /// </summary>
    public static bool IsSupportedCustomLoopbackEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri ||
            !endpoint.IsLoopback ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = endpoint.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HostEquals(Uri endpoint, string expected) =>
        string.Equals(endpoint.IdnHost, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsOfficialQwenHost(string host)
    {
        if (QwenDashScopeHosts.Contains(host))
        {
            return true;
        }

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return labels.Length == 5 &&
            labels[0].Length > 0 &&
            QwenWorkspaceRegions.Contains(labels[1]) &&
            string.Equals(labels[2], "maas", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(labels[3], "aliyuncs", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(labels[4], "com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasOfficialOpenAiPath(string presetId, string absolutePath)
    {
        var path = absolutePath.TrimEnd('/');
        return presetId switch
        {
            DeepSeekId => IsBaseOrChatPath(path, string.Empty) || IsBaseOrChatPath(path, "/v1"),
            QwenId => IsBaseOrChatPath(path, "/compatible-mode/v1"),
            XiaomiMimoId or MiniMaxId or KimiId or OpenAiId => IsBaseOrChatPath(path, "/v1"),
            DoubaoId => IsBaseOrChatPath(path, "/api/v3"),
            ZhipuGlmId => IsBaseOrChatPath(path, "/api/paas/v4"),
            _ => false
        };
    }

    private static bool IsBaseOrChatPath(string path, string basePath) =>
        string.Equals(path, basePath, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, $"{basePath}/chat/completions", StringComparison.OrdinalIgnoreCase);
}
