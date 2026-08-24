namespace LocaleSmith.Core.Models;

public sealed record ModelSource
{
    public ModelSource(
        string id,
        string displayName,
        ModelProviderKind provider,
        Uri endpoint,
        string modelName,
        string? apiKeyReference = null,
        string? presetId = null,
        OpenAiTokenLimitParameter? tokenLimitParameter = null)
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
}
