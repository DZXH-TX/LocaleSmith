namespace LocaleSmith.Core.Models;

public sealed record ModelResponse
{
    public ModelResponse(
        string content,
        string? model = null,
        long? inputTokens = null,
        long? outputTokens = null,
        IReadOnlyList<ModelToolCall>? toolCalls = null,
        string? reasoningContent = null,
        long? totalTokens = null,
        ModelTokenUsage? usage = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (toolCalls?.Any(static call => call is null) == true)
        {
            throw new ArgumentException("Tool calls cannot contain null values.", nameof(toolCalls));
        }

        if (toolCalls is { Count: > 32 })
        {
            throw new ArgumentOutOfRangeException(nameof(toolCalls), "A response cannot contain more than 32 tool calls.");
        }

        if (content.Length == 0 && toolCalls is not { Count: > 0 })
        {
            throw new ArgumentException("A model response must contain text or at least one tool call.", nameof(content));
        }

        if (usage is not null &&
            (inputTokens is not null || outputTokens is not null || totalTokens is not null))
        {
            throw new ArgumentException(
                "Specify either structured usage or individual token counts, not both.",
                nameof(usage));
        }

        if (usage is { ProviderCallCount: 0 })
        {
            throw new ArgumentException(
                "A model response with structured usage must represent at least one provider call.",
                nameof(usage));
        }

        Content = content;
        Model = model;
        Usage = usage ?? ModelTokenUsage.FromProviderResponse(inputTokens, outputTokens, totalTokens);
        ToolCalls = toolCalls?.ToArray() ?? [];
        ReasoningContent = reasoningContent;
    }

    public string Content { get; }

    public string? Model { get; }

    public long? InputTokens => Usage?.InputTokens;

    public long? OutputTokens => Usage?.OutputTokens;

    public long? TotalTokens => Usage?.TotalTokens;

    public ModelTokenUsage? Usage { get; }

    public IReadOnlyList<ModelToolCall> ToolCalls { get; }

    /// <summary>
    /// Provider-private reasoning state for protocol replay. Callers must not present it as response content.
    /// </summary>
    public string? ReasoningContent { get; }
}
