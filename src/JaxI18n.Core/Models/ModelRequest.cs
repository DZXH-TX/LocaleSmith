namespace JaxI18n.Core.Models;

public sealed record ModelRequest
{
    public ModelRequest(
        IReadOnlyList<ModelMessage> messages,
        double? temperature = null,
        int? maxTokens = null,
        IReadOnlyList<ModelToolDefinition>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            throw new ArgumentException("At least one message is required.", nameof(messages));
        }

        if (messages.Any(static message => message is null))
        {
            throw new ArgumentException("Messages cannot contain null values.", nameof(messages));
        }

        if (temperature is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(temperature), "Temperature must be between 0 and 2.");
        }

        if (maxTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens), "Max tokens must be positive.");
        }

        if (tools?.Any(static tool => tool is null) == true)
        {
            throw new ArgumentException("Tools cannot contain null values.", nameof(tools));
        }

        if (tools is { Count: > 64 })
        {
            throw new ArgumentOutOfRangeException(nameof(tools), "A request cannot expose more than 64 tools.");
        }

        if (tools is not null && tools.Select(static tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count)
        {
            throw new ArgumentException("Tool names must be unique within a request.", nameof(tools));
        }

        Messages = messages.ToArray();
        Temperature = temperature;
        MaxTokens = maxTokens;
        Tools = tools?.ToArray() ?? [];
    }

    public IReadOnlyList<ModelMessage> Messages { get; }

    public double? Temperature { get; }

    public int? MaxTokens { get; }

    public IReadOnlyList<ModelToolDefinition> Tools { get; }
}
