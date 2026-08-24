using System.Text.Json;

namespace LocaleSmith.Core.Models;

public sealed record ModelToolDefinition
{
    private const int MaximumSchemaCharacters = 64 * 1024;

    public ModelToolDefinition(string name, string description, JsonElement inputSchema)
    {
        ValidateToolName(name, nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (description.Length > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(description), "Tool descriptions cannot exceed 4096 characters.");
        }

        if (inputSchema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A tool input schema must be a JSON object.", nameof(inputSchema));
        }

        if (inputSchema.GetRawText().Length > MaximumSchemaCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(inputSchema), "A tool input schema is too large.");
        }

        Name = name;
        Description = description;
        InputSchema = inputSchema.Clone();
    }

    public string Name { get; }

    public string Description { get; }

    public JsonElement InputSchema { get; }

    internal static void ValidateToolName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 64 || value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArgumentException(
                "Tool names must contain 1-64 ASCII letters, digits, underscores, or hyphens.",
                parameterName);
        }
    }
}

public sealed record ModelToolCall
{
    private const int MaximumArgumentsCharacters = 64 * 1024;

    public ModelToolCall(string id, string name, JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 128 || id.Any(char.IsControl))
        {
            throw new ArgumentException("Tool-call ids must contain at most 128 non-control characters.", nameof(id));
        }

        ModelToolDefinition.ValidateToolName(name, nameof(name));
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool-call arguments must be a JSON object.", nameof(arguments));
        }

        if (arguments.GetRawText().Length > MaximumArgumentsCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments), "Tool-call arguments are too large.");
        }

        Id = id;
        Name = name;
        Arguments = arguments.Clone();
    }

    public string Id { get; }

    public string Name { get; }

    public JsonElement Arguments { get; }
}

public sealed record ModelToolResult(
    string ToolCallId,
    string ToolName,
    string Content,
    bool IsError = false,
    Guid? PublicTaskId = null)
{
    public ModelToolResult Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ToolCallId);
        ModelToolDefinition.ValidateToolName(ToolName, nameof(ToolName));
        ArgumentNullException.ThrowIfNull(Content);
        return this;
    }
}
