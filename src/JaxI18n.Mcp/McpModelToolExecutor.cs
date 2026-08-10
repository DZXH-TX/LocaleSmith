using System.Text.Json;
using JaxI18n.Core.Abstractions;
using JaxI18n.Core.Models;

namespace JaxI18n.Mcp;

/// <summary>
/// Adapts the read-only MCP context and command-proposal tools to provider-native function
/// calling. The high-risk cli.execute tool is intentionally never exposed through this bridge;
/// execution still requires the UI-owned confirmation flow and a single-use approval token.
/// </summary>
public sealed class McpModelToolExecutor : IModelToolExecutor
{
    private const string SystemContextModelName = "system_context";
    private const string CliProposeModelName = "cli_propose";
    private readonly McpToolCatalog _catalog;
    private readonly McpServerOptions _options;

    public McpModelToolExecutor(
        ISystemPromptContextProvider contextProvider,
        ICliCommandPolicy commandPolicy,
        McpServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(contextProvider);
        ArgumentNullException.ThrowIfNull(commandPolicy);
        _options = options ?? new McpServerOptions();
        _options.Validate();
        _catalog = new McpToolCatalog(contextProvider, commandPolicy, cliRunner: null, _options);
        Tools = CreateDefinitions();
    }

    public IReadOnlyList<ModelToolDefinition> Tools { get; }

    public async Task<ModelToolResult> ExecuteAsync(
        ModelToolCall toolCall,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        string mcpName = toolCall.Name switch
        {
            SystemContextModelName => "system.context",
            CliProposeModelName => "cli.propose",
            _ => throw new InvalidOperationException($"Model tool '{toolCall.Name}' is not exposed by the MCP bridge.")
        };

        object result = await _catalog
            .CallAsync(mcpName, toolCall.Arguments, cancellationToken)
            .ConfigureAwait(false);
        string serialized = JsonSerializer.Serialize(result);
        using var document = JsonDocument.Parse(serialized);
        bool isError = document.RootElement.TryGetProperty("isError", out JsonElement errorValue) &&
            errorValue.ValueKind == JsonValueKind.True;
        string bounded = OutputSanitizer.Sanitize(serialized, _options.MaximumOutputCharacters);
        return new ModelToolResult(toolCall.Id, toolCall.Name, bounded, isError);
    }

    private static IReadOnlyList<ModelToolDefinition> CreateDefinitions()
    {
        using var emptySchema = JsonDocument.Parse("""{"type":"object","additionalProperties":false}""");
        using var proposalSchema = JsonDocument.Parse(
            """
            {
              "type":"object",
              "properties":{
                "executable":{"type":"string","minLength":1,"maxLength":260},
                "arguments":{"type":"array","maxItems":64,"items":{"type":"string","maxLength":4096}},
                "workingDirectory":{"type":"string","minLength":1,"maxLength":1024},
                "timeoutSeconds":{"type":"integer","minimum":1,"maximum":30}
              },
              "required":["executable","workingDirectory"],
              "additionalProperties":false
            }
            """);
        return
        [
            new ModelToolDefinition(
                SystemContextModelName,
                "Read sanitized Windows, terminal, shell, and allowlisted environment context. Read-only.",
                emptySchema.RootElement),
            new ModelToolDefinition(
                CliProposeModelName,
                "Validate and summarize a CLI proposal. Never executes and never issues approval tokens.",
                proposalSchema.RootElement)
        ];
    }
}
