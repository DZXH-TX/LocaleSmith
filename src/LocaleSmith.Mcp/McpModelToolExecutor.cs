using System.Collections.ObjectModel;
using System.Text.Json;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Mcp;

/// <summary>
/// Adapts the read-only MCP context and command-proposal tools to provider-native function
/// calling. The high-risk cli.execute tool is intentionally never exposed through this bridge;
/// execution still requires the UI-owned confirmation flow and a single-use approval token.
/// </summary>
public sealed class McpModelToolExecutor : IModelToolExecutor
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private const string SystemContextModelName = "system_context";
    private const string CliProposeModelName = "cli_propose";
    private const string ProjectGetActiveModelName = "project_get_active";
    private const string ArchiveInspectModelName = "archive_inspect";
    private const string TranslationStartModelName = "translation_start";
    private const string TaskStatusModelName = "task_status";
    private const string TaskCancelModelName = "task_cancel";
    private readonly McpToolCatalog _catalog;
    private readonly McpServerOptions _options;
    private readonly bool _projectToolsEnabled;

    public McpModelToolExecutor(
        ISystemPromptContextProvider contextProvider,
        ICliCommandPolicy commandPolicy,
        McpServerOptions? options = null,
        IProjectMcpBackend? projectBackend = null)
    {
        ArgumentNullException.ThrowIfNull(contextProvider);
        ArgumentNullException.ThrowIfNull(commandPolicy);
        _options = options ?? new McpServerOptions();
        _options.Validate();
        _projectToolsEnabled = projectBackend is not null;
        _catalog = new McpToolCatalog(
            contextProvider,
            commandPolicy,
            cliRunner: null,
            _options,
            projectBackend);
        Tools = CreateDefinitions(_projectToolsEnabled);
    }

    public IReadOnlyList<ModelToolDefinition> Tools { get; }

    public async Task<ModelToolResult> ExecuteAsync(
        ModelToolCall toolCall,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteCoreAsync(toolCall, projectScopeId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModelToolResult> ExecuteProjectScopedAsync(
        ModelToolCall toolCall,
        Guid projectScopeId,
        CancellationToken cancellationToken = default)
    {
        if (projectScopeId == Guid.Empty)
        {
            throw new ArgumentException("A project tool scope cannot be empty.", nameof(projectScopeId));
        }

        return await ExecuteCoreAsync(toolCall, projectScopeId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelToolResult> ExecuteCoreAsync(
        ModelToolCall toolCall,
        Guid? projectScopeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        string mcpName = ResolveMcpName(toolCall.Name);

        object result = await _catalog
            .CallAsync(mcpName, toolCall.Arguments, projectScopeId, cancellationToken)
            .ConfigureAwait(false);
        string serialized = JsonSerializer.Serialize(result, SerializerOptions);
        using var document = JsonDocument.Parse(serialized);
        bool isError = document.RootElement.TryGetProperty("isError", out JsonElement errorValue) &&
            errorValue.ValueKind == JsonValueKind.True;
        string bounded = OutputSanitizer.Sanitize(serialized, _options.MaximumOutputCharacters);
        return new ModelToolResult(toolCall.Id, toolCall.Name, bounded, isError);
    }

    private string ResolveMcpName(string modelToolName) => modelToolName switch
    {
        SystemContextModelName => "system.context",
        CliProposeModelName => "cli.propose",
        ProjectGetActiveModelName when _projectToolsEnabled => "project.get_active",
        ArchiveInspectModelName when _projectToolsEnabled => "archive.inspect",
        TranslationStartModelName when _projectToolsEnabled => "translation.start",
        TaskStatusModelName when _projectToolsEnabled => "task.status",
        TaskCancelModelName when _projectToolsEnabled => "task.cancel",
        _ => throw new InvalidOperationException($"Model tool '{modelToolName}' is not exposed by the MCP bridge.")
    };

    private static ReadOnlyCollection<ModelToolDefinition> CreateDefinitions(bool includeProjectTools)
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
        var tools = new List<ModelToolDefinition>
        {
            new ModelToolDefinition(
                SystemContextModelName,
                "Read sanitized Windows, terminal, shell, and allowlisted environment context. Read-only.",
                emptySchema.RootElement),
            new ModelToolDefinition(
                CliProposeModelName,
                "Validate and summarize a CLI proposal. Never executes and never issues approval tokens.",
                proposalSchema.RootElement)
        };
        if (!includeProjectTools)
        {
            return tools.AsReadOnly();
        }

        using var projectIdSchema = JsonDocument.Parse(
            """
            {
              "type":"object",
              "properties":{"projectId":{"type":"string","format":"uuid"}},
              "required":["projectId"],
              "additionalProperties":false
            }
            """);
        using var taskIdSchema = JsonDocument.Parse(
            """
            {
              "type":"object",
              "properties":{"taskId":{"type":"string","format":"uuid"}},
              "required":["taskId"],
              "additionalProperties":false
            }
            """);
        using var translationSchema = JsonDocument.Parse(
            """
            {
              "type":"object",
              "properties":{
                "projectId":{"type":"string","format":"uuid"},
                "objective":{"type":"string","minLength":1,"maxLength":2048},
                "targetLanguage":{"type":"string","minLength":2,"maxLength":32},
                "style":{"type":"string","enum":["formal","informal"]}
              },
              "required":["projectId","objective"],
              "additionalProperties":false
            }
            """);
        tools.Add(new ModelToolDefinition(
            ProjectGetActiveModelName,
            "Read the application-selected LocaleSmith project and opaque identifiers. No host path input is accepted.",
            emptySchema.RootElement));
        tools.Add(new ModelToolDefinition(
            ArchiveInspectModelName,
            "Safely inspect only the source artifact registered for the active LocaleSmith project.",
            projectIdSchema.RootElement));
        tools.Add(new ModelToolDefinition(
            TranslationStartModelName,
            "Start LocaleSmith's full transactional translation pipeline for the active project. Rejects duplicate active work.",
            translationSchema.RootElement));
        tools.Add(new ModelToolDefinition(
            TaskStatusModelName,
            "Read the real pipeline status of an opaque task in the active LocaleSmith project.",
            taskIdSchema.RootElement));
        tools.Add(new ModelToolDefinition(
            TaskCancelModelName,
            "Request cancellation through the real queue handle for an active-project task.",
            taskIdSchema.RootElement));
        return tools.AsReadOnly();
    }
}
