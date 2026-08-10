namespace LocaleSmith.Core.Models;

public sealed record ModelResponse
{
    public ModelResponse(
        string content,
        string? model = null,
        int? inputTokens = null,
        int? outputTokens = null,
        IReadOnlyList<ModelToolCall>? toolCalls = null,
        string? reasoningContent = null)
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

        Content = content;
        Model = model;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        ToolCalls = toolCalls?.ToArray() ?? [];
        ReasoningContent = reasoningContent;
    }

    public string Content { get; }

    public string? Model { get; }

    public int? InputTokens { get; }

    public int? OutputTokens { get; }

    public IReadOnlyList<ModelToolCall> ToolCalls { get; }

    /// <summary>
    /// Provider-private reasoning state for protocol replay. Callers must not present it as response content.
    /// </summary>
    public string? ReasoningContent { get; }
}
