namespace LocaleSmith.Core.Models;

public sealed record ModelSource
{
    public const int DefaultMaxOutputTokens = 8_000;
    public const int MinimumMaxOutputTokens = 256;
    public const int MaximumMaxOutputTokens = 65_536;
    public const int DefaultMaxSourceCharactersPerRequest = 12_000;
    public const int MinimumMaxSourceCharactersPerRequest = 1_000;
    public const int MaximumMaxSourceCharactersPerRequest = 100_000;

    public ModelSource(
        string id,
        string displayName,
        ModelProviderKind provider,
        Uri endpoint,
        string modelName,
        string? apiKeyReference = null,
        string? presetId = null,
        OpenAiTokenLimitParameter? tokenLimitParameter = null,
        int? maxOutputTokens = null,
        int? maxSourceCharactersPerRequest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        if (!endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Model endpoints must be absolute HTTP or HTTPS URIs.", nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "Model endpoint base URIs cannot contain user information, a query, or a fragment.",
                nameof(endpoint));
        }

        if (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback)
        {
            throw new ArgumentException(
                "Remote model endpoints must use HTTPS; plaintext HTTP is permitted only for loopback services.",
                nameof(endpoint));
        }

        if (tokenLimitParameter is { } explicitParameter && !Enum.IsDefined(explicitParameter))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tokenLimitParameter),
                tokenLimitParameter,
                "Unknown OpenAI-compatible token-limit parameter.");
        }

        if (maxOutputTokens is < MinimumMaxOutputTokens or > MaximumMaxOutputTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputTokens),
                $"Max output tokens must be between {MinimumMaxOutputTokens} and {MaximumMaxOutputTokens}.");
        }

        if (maxSourceCharactersPerRequest is
            < MinimumMaxSourceCharactersPerRequest or
            > MaximumMaxSourceCharactersPerRequest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSourceCharactersPerRequest),
                $"Max source characters per request must be between {MinimumMaxSourceCharactersPerRequest} and " +
                $"{MaximumMaxSourceCharactersPerRequest}.");
        }

        Id = id.Trim();
        DisplayName = displayName.Trim();
        Provider = provider;
        Endpoint = endpoint;
        ModelName = modelName.Trim();
        ApiKeyReference = string.IsNullOrWhiteSpace(apiKeyReference) ? null : apiKeyReference.Trim();
        var preset = ModelProviderPresets.ResolveEffective(provider, presetId, endpoint);
        PresetId = preset.Id;
        TokenLimitParameter = tokenLimitParameter ?? preset.DefaultTokenLimitParameter;
        SupportsCustomTemperature = preset.SupportsCustomTemperature;
        RequiresReasoningContentReplay = preset.RequiresReasoningContentReplay;
        UsesReasoningDetailsReplay = preset.UsesReasoningDetailsReplay;
        RequiresNonNullToolCallContent = preset.RequiresNonNullToolCallContent;
        MaxOutputTokens = maxOutputTokens;
        MaxSourceCharactersPerRequest = maxSourceCharactersPerRequest;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public ModelProviderKind Provider { get; }

    public Uri Endpoint { get; }

    public string ModelName { get; }

    /// <summary>A credential reference only; never the credential value.</summary>
    public string? ApiKeyReference { get; }

    /// <summary>Editable provider-default metadata; transport remains selected by <see cref="Provider"/>.</summary>
    public string PresetId { get; }

    /// <summary>The request field used for a completion limit, or <c>Omit</c> to use the provider default.</summary>
    public OpenAiTokenLimitParameter TokenLimitParameter { get; }

    /// <summary>Whether the selected preset's default model accepts caller-selected sampling temperature.</summary>
    public bool SupportsCustomTemperature { get; }

    /// <summary>Whether provider-private reasoning state must be replayed during multi-step tool calls.</summary>
    public bool RequiresReasoningContentReplay { get; }

    /// <summary>Whether private reasoning state uses MiniMax's structured reasoning_details field.</summary>
    public bool UsesReasoningDetailsReplay { get; }

    /// <summary>Whether assistant tool-call messages must serialize empty content as an empty string.</summary>
    public bool RequiresNonNullToolCallContent { get; }

    /// <summary>Optional per-source response budget. Null preserves the caller's workflow default.</summary>
    public int? MaxOutputTokens { get; }

    /// <summary>Optional per-source batching target. Null preserves the translation-engine default.</summary>
    public int? MaxSourceCharactersPerRequest { get; }
}
