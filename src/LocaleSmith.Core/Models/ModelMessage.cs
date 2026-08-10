namespace LocaleSmith.Core.Models;

public enum ModelMessageRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed record ModelMessage
{
    public ModelMessage(
        ModelMessageRole role,
        string content,
        IReadOnlyList<ModelToolCall>? toolCalls = null,
        string? toolCallId = null,
        string? toolName = null,
        bool toolResultIsError = false,
        string? reasoningContent = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (toolCalls?.Any(static call => call is null) == true)
        {
            throw new ArgumentException("Tool calls cannot contain null values.", nameof(toolCalls));
        }

        if (toolCalls is { Count: > 32 })
        {
            throw new ArgumentOutOfRangeException(nameof(toolCalls), "A message cannot contain more than 32 tool calls.");
        }

        if (role != ModelMessageRole.Assistant && toolCalls is { Count: > 0 })
        {
            throw new ArgumentException("Only assistant messages can contain tool calls.", nameof(toolCalls));
        }

        if (role == ModelMessageRole.Tool)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
            ModelToolDefinition.ValidateToolName(toolName!, nameof(toolName));
        }
        else if (toolCallId is not null || toolName is not null || toolResultIsError)
        {
            throw new ArgumentException("Tool-result metadata is only valid on tool messages.", nameof(toolCallId));
        }

        if (role != ModelMessageRole.Assistant && reasoningContent is not null)
        {
            throw new ArgumentException(
                "Provider reasoning state is only valid on assistant messages.",
                nameof(reasoningContent));
        }

        if (role == ModelMessageRole.Assistant && content.Length == 0 && toolCalls is not { Count: > 0 })
        {
            throw new ArgumentException("An assistant message must contain text or at least one tool call.", nameof(content));
        }

        Role = role;
        Content = content;
        ToolCalls = toolCalls?.ToArray() ?? [];
        ToolCallId = toolCallId;
        ToolName = toolName;
        ToolResultIsError = toolResultIsError;
        ReasoningContent = reasoningContent;
    }

    public ModelMessageRole Role { get; }

    public string Content { get; }

    public IReadOnlyList<ModelToolCall> ToolCalls { get; }

    public string? ToolCallId { get; }

    public string? ToolName { get; }

    public bool ToolResultIsError { get; }

    /// <summary>
    /// Provider-private reasoning state that may be replayed for protocol continuity. It is not user-visible content.
    /// </summary>
    public string? ReasoningContent { get; }
}
