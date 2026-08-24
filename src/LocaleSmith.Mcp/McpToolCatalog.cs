using System.Text.Json;
using System.Text.RegularExpressions;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Mcp;

internal sealed partial class McpToolCatalog
{
    private const int MaximumArgumentCount = 64;
    private const int MaximumArgumentCharacters = 4096;
    private const int MaximumTotalArgumentCharacters = 16 * 1024;
    private readonly ISystemPromptContextProvider _contextProvider;
    private readonly ICliCommandPolicy _commandPolicy;
    private readonly ICliRunner? _cliRunner;
    private readonly McpServerOptions _options;
    private readonly IProjectMcpBackend? _projectBackend;

    public McpToolCatalog(
        ISystemPromptContextProvider contextProvider,
        ICliCommandPolicy commandPolicy,
        ICliRunner? cliRunner,
        McpServerOptions options,
        IProjectMcpBackend? projectBackend = null)
    {
        _contextProvider = contextProvider;
        _commandPolicy = commandPolicy;
        _cliRunner = cliRunner;
        _options = options;
        _projectBackend = projectBackend;
    }

    public object ListTools()
    {
        var tools = new List<object>
        {
            CreateSystemContextDefinition(),
            CreateCliProposeDefinition()
        };
        if (_options.EnableCliExecution)
        {
            tools.Add(CreateCliExecuteDefinition());
        }

        if (_projectBackend is not null)
        {
            tools.Add(CreateProjectGetActiveDefinition());
            tools.Add(CreateArchiveInspectDefinition());
            tools.Add(CreateTranslationStartDefinition());
            tools.Add(CreateTaskStatusDefinition());
            tools.Add(CreateTaskCancelDefinition());
        }

        return new { tools };
    }

    public Task<object> CallAsync(
        string name,
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        CallAsync(name, arguments, projectScopeId: null, cancellationToken);

    public async Task<object> CallAsync(
        string name,
        JsonElement arguments,
        Guid? projectScopeId,
        CancellationToken cancellationToken)
    {
        try
        {
            return name switch
            {
                "system.context" => await GetSystemContextAsync(arguments, cancellationToken).ConfigureAwait(false),
                "cli.propose" => ProposeCli(arguments),
                "cli.execute" when _options.EnableCliExecution =>
                    await ExecuteCliAsync(arguments, cancellationToken).ConfigureAwait(false),
                "project.get_active" when _projectBackend is not null =>
                    await GetActiveProjectAsync(arguments, projectScopeId, cancellationToken).ConfigureAwait(false),
                "archive.inspect" when _projectBackend is not null =>
                    await InspectArchiveAsync(arguments, projectScopeId, cancellationToken).ConfigureAwait(false),
                "translation.start" when _projectBackend is not null =>
                    await StartTranslationAsync(arguments, projectScopeId, cancellationToken).ConfigureAwait(false),
                "task.status" when _projectBackend is not null =>
                    await GetTaskStatusAsync(arguments, projectScopeId, cancellationToken).ConfigureAwait(false),
                "task.cancel" when _projectBackend is not null =>
                    await CancelTaskAsync(arguments, projectScopeId, cancellationToken).ConfigureAwait(false),
                _ => throw new McpUnknownToolException(name)
            };
        }
        catch (ProjectMcpBackendException exception)
        {
            return ToolFailure(exception.Message);
        }
    }

    private async Task<object> GetActiveProjectAsync(
        JsonElement arguments,
        Guid? projectScopeId,
        CancellationToken cancellationToken)
    {
        RequireObjectWithOnly(arguments, []);
        ProjectMcpSnapshot? project = projectScopeId is { } scopedProjectId
            ? await _projectBackend!.GetProjectAsync(scopedProjectId, cancellationToken).ConfigureAwait(false)
            : await _projectBackend!.GetActiveProjectAsync(cancellationToken).ConfigureAwait(false);
        project = project is null ? null : Sanitize(project);
        return project is null
            ? ToolFailure("No active LocaleSmith project is available. Add or select a package in the application first.")
            : ToolSuccess(project, $"Active project: {project.ProjectId}. Source: {project.SourceName}.");
    }

    private async Task<object> InspectArchiveAsync(
        JsonElement arguments,
        Guid? projectScopeId,
        CancellationToken cancellationToken)
    {
        RequireObjectWithOnly(arguments, ["projectId"]);
        Guid projectId = RequireGuid(arguments, "projectId");
        RequireProjectScope(projectScopeId, projectId);
        ArchiveMcpInspection inspection = await _projectBackend!
            .InspectArchiveAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        inspection = Sanitize(inspection);
        return ToolSuccess(
            inspection,
            $"Inspected active project {inspection.ProjectId}: {inspection.Loader}/{inspection.ModId}, " +
            $"{inspection.EntryCount} archive entries and {inspection.ResourceCount} resources.");
    }

    private async Task<object> StartTranslationAsync(
        JsonElement arguments,
        Guid? projectScopeId,
        CancellationToken cancellationToken)
    {
        RequireObjectWithOnly(
            arguments,
            ["projectId", "objective", "modelSourceId", "targetLanguage", "style"]);
        string objective = RequireString(arguments, "objective", 1, 2048);
        string? modelSourceId = OptionalString(arguments, "modelSourceId", 1, 256);
        string? targetLanguage = OptionalString(arguments, "targetLanguage", 2, 32);
        string? style = OptionalString(arguments, "style", 1, 32);
        RejectUnsafeText(objective, "objective");
        RejectUnsafeText(modelSourceId ?? string.Empty, "modelSourceId");
        RejectUnsafeText(targetLanguage ?? string.Empty, "targetLanguage");
        RejectUnsafeText(style ?? string.Empty, "style");
        Guid projectId = RequireGuid(arguments, "projectId");
        RequireProjectScope(projectScopeId, projectId);
        var request = new TranslationMcpStartRequest(
            projectId,
            objective,
            modelSourceId,
            targetLanguage,
            style);
        TaskMcpSnapshot task = await _projectBackend!
            .StartTranslationAsync(request, cancellationToken)
            .ConfigureAwait(false);
        task = Sanitize(task);
        return ToolSuccess(
            task,
            $"Translation task {task.TaskId} was accepted for active project {task.ProjectId}. " +
            $"Current status: {task.Status}.");
    }

    private async Task<object> GetTaskStatusAsync(
        JsonElement arguments,
        Guid? projectScopeId,
        CancellationToken cancellationToken)
    {
        RequireObjectWithOnly(arguments, ["taskId"]);
        Guid taskId = RequireGuid(arguments, "taskId");
        TaskMcpSnapshot? task = projectScopeId is { } scopedProjectId
            ? await _projectBackend!
                .GetTaskAsync(scopedProjectId, taskId, cancellationToken)
                .ConfigureAwait(false)
            : await _projectBackend!.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        task = task is null ? null : Sanitize(task);
        return task is null
            ? ToolFailure("The task was not found in the active LocaleSmith project.")
            : ToolSuccess(
                task,
                $"Task {task.TaskId}: {task.Status}, stage {task.Stage}, progress {task.Progress:P0}.");
    }

    private async Task<object> CancelTaskAsync(
        JsonElement arguments,
        Guid? projectScopeId,
        CancellationToken cancellationToken)
    {
        RequireObjectWithOnly(arguments, ["taskId"]);
        Guid taskId = RequireGuid(arguments, "taskId");
        TaskMcpSnapshot task = projectScopeId is { } scopedProjectId
            ? await _projectBackend!
                .CancelTaskAsync(scopedProjectId, taskId, cancellationToken)
                .ConfigureAwait(false)
            : await _projectBackend!.CancelTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        task = Sanitize(task);
        return ToolSuccess(
            task,
            $"Cancellation was requested for task {task.TaskId}. The transactional pipeline will roll back safely.");
    }

    private static void RequireProjectScope(Guid? expectedProjectId, Guid requestedProjectId)
    {
        if (expectedProjectId is { } expected && expected != requestedProjectId)
        {
            throw new ProjectMcpBackendException(
                "The opaque project id does not identify this assistant session's project.");
        }
    }

    private async Task<object> GetSystemContextAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        RequireObjectWithOnly(arguments, []);
        var rawContext = await _contextProvider.BuildAsync(cancellationToken).ConfigureAwait(false);
        var sanitized = OutputSanitizer.Sanitize(rawContext, _options.MaximumOutputCharacters);
        var structured = new
        {
            context = sanitized,
            truncated = rawContext.Length > _options.MaximumOutputCharacters
        };
        return ToolSuccess(structured, sanitized);
    }

    private object ProposeCli(JsonElement arguments)
    {
        var command = ParseCommand(arguments, requireApprovalToken: false, out _);
        CliPolicyDecision decision;
        try
        {
            decision = _commandPolicy.Evaluate(command);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ToolFailure($"Command validation failed: {SanitizeExceptionMessage(exception)}");
        }

        var structured = new
        {
            allowed = decision.IsAllowed,
            violation = decision.Violation.ToString(),
            reason = OutputSanitizer.Sanitize(decision.Reason, 2048),
            command = CommandSummary(command, decision.ResolvedExecutable),
            approval = new
            {
                required = true,
                tokenIssued = false,
                userMustAcknowledgeRisk = true,
                singleUseTokenMustComeFromUi = true,
                summary = "Review the complete command in the UI and explicitly acknowledge the risk. This MCP tool never issues approval tokens or executes commands."
            }
        };
        var text = decision.IsAllowed
            ? "Command passed the current policy. UI review and a separate single-use approval token are still required; nothing was executed."
            : $"Command was rejected by policy ({decision.Violation}); nothing was executed.";
        return ToolSuccess(structured, text);
    }

    private async Task<object> ExecuteCliAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (_cliRunner is null)
        {
            return ToolFailure("CLI execution is not configured for this MCP server.");
        }

        CliCommand command;
        string? approvalToken;
        try
        {
            command = ParseCommand(arguments, requireApprovalToken: true, out approvalToken);
        }
        catch (McpToolInputException exception)
        {
            return ToolFailure(exception.Message);
        }

        // The MCP server cannot mint tokens. The UI-owned approval service creates the
        // command-bound, expiring, single-use token that the runner consumes atomically.
        CliExecutionResult result;
        try
        {
            result = await _cliRunner.ExecuteAsync(command, approvalToken!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolFailure("The approved CLI runner failed without producing an execution result.");
        }

        var standardOutput = OutputSanitizer.Sanitize(result.StandardOutput, _options.MaximumOutputCharacters);
        var standardError = OutputSanitizer.Sanitize(result.StandardError, _options.MaximumOutputCharacters);
        var reason = OutputSanitizer.Sanitize(result.Reason, 2048);
        var structured = new
        {
            status = result.Status.ToString(),
            result.ExitCode,
            standardOutput,
            standardError,
            durationMilliseconds = Math.Max(0, result.Duration.TotalMilliseconds),
            reason
        };
        var isError = result.Status is not CliExecutionStatus.Completed;
        var exitCode = result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a";
        var text = $"CLI execution status: {result.Status}. Exit code: {exitCode}.";
        return ToolResult(structured, text, isError);
    }

    private static CliCommand ParseCommand(
        JsonElement arguments,
        bool requireApprovalToken,
        out string? approvalToken)
    {
        var allowed = requireApprovalToken
            ? new[] { "executable", "arguments", "workingDirectory", "timeoutSeconds", "approvalToken" }
            : new[] { "executable", "arguments", "workingDirectory", "timeoutSeconds" };
        RequireObjectWithOnly(arguments, allowed);

        var executable = RequireString(arguments, "executable", 1, 260);
        var workingDirectory = RequireString(arguments, "workingDirectory", 1, 1024);
        RejectUnsafeText(executable, "executable");
        RejectUnsafeText(workingDirectory, "workingDirectory");

        var commandArguments = new List<string>();
        if (arguments.TryGetProperty("arguments", out var argumentArray))
        {
            if (argumentArray.ValueKind != JsonValueKind.Array || argumentArray.GetArrayLength() > MaximumArgumentCount)
            {
                throw new McpToolInputException($"arguments must be an array containing at most {MaximumArgumentCount} strings.");
            }

            var totalCharacters = 0;
            foreach (var element in argumentArray.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } value || value.Length > MaximumArgumentCharacters)
                {
                    throw new McpToolInputException(
                        $"Every command argument must be a string of at most {MaximumArgumentCharacters} characters.");
                }

                RejectUnsafeText(value, "arguments");
                totalCharacters += value.Length;
                if (totalCharacters > MaximumTotalArgumentCharacters)
                {
                    throw new McpToolInputException(
                        $"The combined command arguments exceed {MaximumTotalArgumentCharacters} characters.");
                }

                commandArguments.Add(value);
            }
        }

        var timeoutSeconds = 30;
        if (arguments.TryGetProperty("timeoutSeconds", out var timeoutElement))
        {
            if (timeoutElement.ValueKind != JsonValueKind.Number ||
                !timeoutElement.TryGetInt32(out timeoutSeconds) ||
                timeoutSeconds is < 1 or > 30)
            {
                throw new McpToolInputException("timeoutSeconds must be an integer from 1 through 30.");
            }
        }

        approvalToken = null;
        if (requireApprovalToken)
        {
            approvalToken = RequireString(arguments, "approvalToken", 43, 43);
            if (!ApprovalTokenPattern().IsMatch(approvalToken))
            {
                throw new McpToolInputException(
                    "A valid UI-issued, 256-bit, base64url single-use approvalToken is required.");
            }
        }

        try
        {
            return new CliCommand(executable, commandArguments, workingDirectory, TimeSpan.FromSeconds(timeoutSeconds));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new McpToolInputException($"Invalid command: {SanitizeExceptionMessage(exception)}");
        }
    }

    private static object CommandSummary(CliCommand command, string? resolvedExecutable) => new
    {
        executable = command.Executable,
        arguments = RedactArguments(command.Arguments),
        workingDirectory = command.WorkingDirectory,
        timeoutSeconds = command.Timeout.TotalSeconds,
        display = command.ToDisplayString(redactSensitiveValues: true),
        resolvedExecutable
    };

    private static string[] RedactArguments(IReadOnlyList<string> arguments)
    {
        var redacted = new string[arguments.Count];
        var redactNext = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (redactNext)
            {
                redacted[index] = "***REDACTED***";
                redactNext = false;
                continue;
            }

            redacted[index] = OutputSanitizer.Sanitize(argument, MaximumArgumentCharacters);
            if (SensitiveOptionPattern().IsMatch(argument) && !argument.Contains('=', StringComparison.Ordinal))
            {
                redactNext = true;
            }
        }

        return redacted;
    }

    private static object ToolSuccess(object structuredContent, string text) =>
        ToolResult(structuredContent, text, isError: false);

    private static object ToolFailure(string text) => new
    {
        content = new[] { new { type = "text", text = OutputSanitizer.Sanitize(text, 4096) } },
        isError = true
    };

    private static object ToolResult(object structuredContent, string text, bool isError) => new
    {
        content = new[] { new { type = "text", text = OutputSanitizer.Sanitize(text, 4096) } },
        structuredContent,
        isError
    };

    private static string RequireString(JsonElement source, string propertyName, int minimumLength, int maximumLength)
    {
        if (!source.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            element.GetString() is not { } value ||
            value.Length < minimumLength ||
            value.Length > maximumLength)
        {
            throw new McpToolInputException(
                $"{propertyName} must be a string between {minimumLength} and {maximumLength} characters.");
        }

        return value;
    }

    private static string? OptionalString(
        JsonElement source,
        string propertyName,
        int minimumLength,
        int maximumLength)
    {
        if (!source.TryGetProperty(propertyName, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String ||
            element.GetString() is not { } value ||
            value.Length < minimumLength ||
            value.Length > maximumLength)
        {
            throw new McpToolInputException(
                $"{propertyName} must be a string between {minimumLength} and {maximumLength} characters when supplied.");
        }

        return value;
    }

    private static Guid RequireGuid(JsonElement source, string propertyName)
    {
        string value = RequireString(source, propertyName, 36, 36);
        if (!Guid.TryParseExact(value, "D", out Guid result) || result == Guid.Empty)
        {
            throw new McpToolInputException($"{propertyName} must be a non-empty UUID in canonical form.");
        }

        return result;
    }

    private static void RequireObjectWithOnly(JsonElement source, IReadOnlyCollection<string> allowedProperties)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            throw new McpToolInputException("Tool arguments must be a JSON object.");
        }

        foreach (var property in source.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new McpToolInputException($"Unknown tool argument '{property.Name}'.");
            }
        }
    }

    private static void RejectUnsafeText(string value, string propertyName)
    {
        if (value.Any(static character => char.IsControl(character)))
        {
            throw new McpToolInputException($"{propertyName} cannot contain control characters.");
        }
    }

    private static string SanitizeExceptionMessage(Exception exception) =>
        OutputSanitizer.Sanitize(exception.Message, 1024);

    private static ProjectMcpSnapshot Sanitize(ProjectMcpSnapshot value) => value with
    {
        SourceName = OutputSanitizer.Sanitize(value.SourceName, 512),
        ModId = value.ModId is null ? null : OutputSanitizer.Sanitize(value.ModId, 256),
        Loader = value.Loader is null ? null : OutputSanitizer.Sanitize(value.Loader, 128),
        ActiveTaskStatus = value.ActiveTaskStatus is null
            ? null
            : OutputSanitizer.Sanitize(value.ActiveTaskStatus, 128)
    };

    private static ArchiveMcpInspection Sanitize(ArchiveMcpInspection value) => value with
    {
        SourceName = OutputSanitizer.Sanitize(value.SourceName, 512),
        ModId = OutputSanitizer.Sanitize(value.ModId, 256),
        Loader = OutputSanitizer.Sanitize(value.Loader, 128),
        SignatureStatus = OutputSanitizer.Sanitize(value.SignatureStatus, 128),
        Warnings = value.Warnings
            .Take(32)
            .Select(static warning => OutputSanitizer.Sanitize(warning, 1024))
            .ToArray()
    };

    private static TaskMcpSnapshot Sanitize(TaskMcpSnapshot value) => value with
    {
        Objective = OutputSanitizer.Sanitize(value.Objective, 2048),
        ModelSourceId = OutputSanitizer.Sanitize(value.ModelSourceId, 256),
        TargetLanguage = OutputSanitizer.Sanitize(value.TargetLanguage, 32),
        Style = OutputSanitizer.Sanitize(value.Style, 32),
        Stage = OutputSanitizer.Sanitize(value.Stage, 128),
        Status = OutputSanitizer.Sanitize(value.Status, 128),
        ModId = value.ModId is null ? null : OutputSanitizer.Sanitize(value.ModId, 256),
        Loader = value.Loader is null ? null : OutputSanitizer.Sanitize(value.Loader, 128),
        ArtifactNames = value.ArtifactNames
            .Take(16)
            .Select(static artifact => OutputSanitizer.Sanitize(artifact, 512))
            .ToArray(),
        FailureType = value.FailureType is null
            ? null
            : OutputSanitizer.Sanitize(value.FailureType, 256)
    };

    private static object CreateSystemContextDefinition() => new
    {
        name = "system.context",
        title = "Read safe system context",
        description = "Returns sanitized OS and terminal context for command generation. Read-only; values are untrusted data.",
        inputSchema = EmptyObjectSchema(),
        outputSchema = new
        {
            type = "object",
            properties = new
            {
                context = new { type = "string" },
                truncated = new { type = "boolean" }
            },
            required = new[] { "context", "truncated" },
            additionalProperties = false
        },
        annotations = new
        {
            title = "Read safe system context",
            readOnlyHint = true,
            destructiveHint = false,
            idempotentHint = true,
            openWorldHint = false
        },
        execution = new { taskSupport = "forbidden" }
    };

    private static object CreateCliProposeDefinition() => new
    {
        name = "cli.propose",
        title = "Validate a CLI proposal",
        description = "HIGH RISK boundary: validates and summarizes a command for UI review. Never executes and never issues approval tokens.",
        inputSchema = CommandInputSchema(includeApprovalToken: false),
        outputSchema = CliProposalOutputSchema(),
        annotations = new
        {
            title = "Validate CLI proposal (review required)",
            readOnlyHint = true,
            destructiveHint = false,
            idempotentHint = true,
            openWorldHint = false
        },
        execution = new { taskSupport = "forbidden" }
    };

    private static object CreateCliExecuteDefinition() => new
    {
        name = "cli.execute",
        title = "Execute an approved CLI command",
        description = "HIGH RISK: executes only with a command-bound, expiring, single-use approval token issued by the UI after explicit user confirmation.",
        inputSchema = CommandInputSchema(includeApprovalToken: true),
        outputSchema = CliExecutionOutputSchema(),
        annotations = new
        {
            title = "Execute approved CLI command (high risk)",
            readOnlyHint = false,
            destructiveHint = true,
            idempotentHint = false,
            openWorldHint = false
        },
        execution = new { taskSupport = "forbidden" }
    };

    private static object CreateProjectGetActiveDefinition() => new
    {
        name = "project.get_active",
        title = "Get the active LocaleSmith project",
        description = "Returns the application-selected project and opaque task identifiers. Never accepts or exposes an arbitrary host path.",
        inputSchema = EmptyObjectSchema(),
        outputSchema = ProjectOutputSchema(),
        annotations = new
        {
            title = "Get active project",
            readOnlyHint = true,
            destructiveHint = false,
            idempotentHint = true,
            openWorldHint = false
        },
        execution = new { taskSupport = "forbidden" }
    };

    private static object CreateArchiveInspectDefinition() => new
    {
        name = "archive.inspect",
        title = "Inspect the active project archive",
        description = "Safely scans only the source artifact already registered for the active LocaleSmith project. No host path argument is accepted.",
        inputSchema = IdentifierInputSchema("projectId"),
        outputSchema = ArchiveInspectionOutputSchema(),
        annotations = new
        {
            title = "Inspect active project archive",
            readOnlyHint = true,
            destructiveHint = false,
            idempotentHint = true,
            openWorldHint = false
        },
        execution = new { taskSupport = "forbidden" }
    };

    private static object CreateTranslationStartDefinition() => new
    {
        name = "translation.start",
        title = "Start a project translation",
        description = "Starts LocaleSmith's full inspect, extract, translate, repack, verify, and commit pipeline for the active project. The source remains immutable.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                projectId = IdentifierSchema(),
                objective = new { type = "string", minLength = 1, maxLength = 2048 },
                modelSourceId = new { type = "string", minLength = 1, maxLength = 256 },
                targetLanguage = new { type = "string", minLength = 2, maxLength = 32 },
                style = new
                {
                    type = "string",
                    @enum = new[] { "formal", "informal" }
                }
            },
            required = new[] { "projectId", "objective" },
            additionalProperties = false
        },
        outputSchema = TaskOutputSchema(),
        annotations = new
        {
            title = "Start active-project translation",
            readOnlyHint = false,
            destructiveHint = false,
            idempotentHint = false,
            openWorldHint = true
        },
        execution = new { taskSupport = "forbidden" }
    };

    private static object CreateTaskStatusDefinition() => new
    {
        name = "task.status",
        title = "Read project task status",
        description = "Returns the real pipeline status of an opaque task belonging to the active LocaleSmith project.",
        inputSchema = IdentifierInputSchema("taskId"),
        outputSchema = TaskOutputSchema(),
        annotations = new
        {
            title = "Read project task status",
            readOnlyHint = true,
            destructiveHint = false,
            idempotentHint = true,
            openWorldHint = false
        },
        execution = new { taskSupport = "forbidden" }
    };

    private static object CreateTaskCancelDefinition() => new
    {
        name = "task.cancel",
        title = "Cancel a project task",
        description = "Requests cancellation through the task's real queue handle. LocaleSmith performs transactional rollback before reporting cancellation.",
        inputSchema = IdentifierInputSchema("taskId"),
        outputSchema = TaskOutputSchema(),
        annotations = new
        {
            title = "Cancel project task",
            readOnlyHint = false,
            destructiveHint = true,
            idempotentHint = false,
            openWorldHint = false
        },
        execution = new { taskSupport = "forbidden" }
    };

    private static object EmptyObjectSchema() => new
    {
        type = "object",
        additionalProperties = false
    };

    private static object IdentifierSchema() => new
    {
        type = "string",
        minLength = 36,
        maxLength = 36,
        pattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"
    };

    private static object IdentifierInputSchema(string propertyName) => new
    {
        type = "object",
        properties = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [propertyName] = IdentifierSchema()
        },
        required = new[] { propertyName },
        additionalProperties = false
    };

    private static object ProjectOutputSchema() => new
    {
        type = "object",
        properties = new
        {
            projectId = IdentifierSchema(),
            sourceName = new { type = "string" },
            modId = NullableStringSchema(),
            loader = NullableStringSchema(),
            activeTaskId = new { type = new[] { "string", "null" } },
            activeTaskStatus = NullableStringSchema()
        },
        required = new[] { "projectId", "sourceName", "modId", "loader", "activeTaskId", "activeTaskStatus" },
        additionalProperties = false
    };

    private static object ArchiveInspectionOutputSchema() => new
    {
        type = "object",
        properties = new
        {
            projectId = IdentifierSchema(),
            sourceName = new { type = "string" },
            modId = new { type = "string" },
            loader = new { type = "string" },
            entryCount = new { type = "integer", minimum = 0 },
            resourceCount = new { type = "integer", minimum = 0 },
            signatureStatus = new { type = "string" },
            usedFilenameFallback = new { type = "boolean" },
            warnings = new { type = "array", items = new { type = "string" } }
        },
        required = new[]
        {
            "projectId", "sourceName", "modId", "loader", "entryCount", "resourceCount",
            "signatureStatus", "usedFilenameFallback", "warnings"
        },
        additionalProperties = false
    };

    private static object TaskOutputSchema() => new
    {
        type = "object",
        properties = new
        {
            taskId = IdentifierSchema(),
            projectId = IdentifierSchema(),
            jobId = new { type = new[] { "string", "null" } },
            objective = new { type = "string" },
            modelSourceId = new { type = "string" },
            targetLanguage = new { type = "string" },
            style = new { type = "string" },
            stage = new { type = "string" },
            progress = new { type = "number", minimum = 0, maximum = 1 },
            status = new { type = "string" },
            modId = NullableStringSchema(),
            loader = NullableStringSchema(),
            artifactNames = new { type = "array", items = new { type = "string" } },
            failureType = NullableStringSchema(),
            inputTokens = NullableIntegerSchema(),
            outputTokens = NullableIntegerSchema(),
            totalTokens = NullableIntegerSchema(),
            providerCallCount = new { type = "integer", minimum = 0 },
            usageComplete = new { type = "boolean" }
        },
        required = new[]
        {
            "taskId", "projectId", "jobId", "objective", "modelSourceId", "targetLanguage", "style",
            "stage", "progress", "status", "modId", "loader", "artifactNames", "failureType",
            "inputTokens", "outputTokens", "totalTokens", "providerCallCount", "usageComplete"
        },
        additionalProperties = false
    };

    private static object NullableStringSchema() => new { type = new[] { "string", "null" } };

    private static object NullableIntegerSchema() => new { type = new[] { "integer", "null" }, minimum = 0 };

    private static object CommandInputSchema(bool includeApprovalToken)
    {
        var properties = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["executable"] = new { type = "string", minLength = 1, maxLength = 260 },
            ["arguments"] = new
            {
                type = "array",
                maxItems = MaximumArgumentCount,
                items = new { type = "string", maxLength = MaximumArgumentCharacters }
            },
            ["workingDirectory"] = new { type = "string", minLength = 1, maxLength = 1024 },
            ["timeoutSeconds"] = new { type = "integer", minimum = 1, maximum = 30 }
        };
        var required = new List<string> { "executable", "workingDirectory" };
        if (includeApprovalToken)
        {
            properties["approvalToken"] = new
            {
                type = "string",
                minLength = 43,
                maxLength = 43,
                pattern = "^[A-Za-z0-9_-]{43}$",
                description = "Single-use token issued by the UI after explicit risk acknowledgement."
            };
            required.Add("approvalToken");
        }

        return new
        {
            type = "object",
            properties,
            required,
            additionalProperties = false
        };
    }

    private static object CliProposalOutputSchema() => new
    {
        type = "object",
        properties = new
        {
            allowed = new { type = "boolean" },
            violation = new { type = "string" },
            reason = new { type = "string" },
            command = new
            {
                type = "object",
                properties = new
                {
                    executable = new { type = "string" },
                    arguments = new { type = "array", items = new { type = "string" } },
                    workingDirectory = new { type = "string" },
                    timeoutSeconds = new { type = "number" },
                    display = new { type = "string" },
                    resolvedExecutable = new { type = new[] { "string", "null" } }
                },
                required = new[]
                {
                    "executable", "arguments", "workingDirectory", "timeoutSeconds", "display", "resolvedExecutable"
                },
                additionalProperties = false
            },
            approval = new
            {
                type = "object",
                properties = new
                {
                    required = new { type = "boolean" },
                    tokenIssued = new { type = "boolean" },
                    userMustAcknowledgeRisk = new { type = "boolean" },
                    singleUseTokenMustComeFromUi = new { type = "boolean" },
                    summary = new { type = "string" }
                },
                required = new[]
                {
                    "required", "tokenIssued", "userMustAcknowledgeRisk", "singleUseTokenMustComeFromUi", "summary"
                },
                additionalProperties = false
            }
        },
        required = new[] { "allowed", "violation", "reason", "command", "approval" },
        additionalProperties = false
    };

    private static object CliExecutionOutputSchema() => new
    {
        type = "object",
        properties = new
        {
            status = new { type = "string" },
            exitCode = new { type = new[] { "integer", "null" } },
            standardOutput = new { type = "string" },
            standardError = new { type = "string" },
            durationMilliseconds = new { type = "number", minimum = 0 },
            reason = new { type = "string" }
        },
        required = new[]
        {
            "status", "exitCode", "standardOutput", "standardError", "durationMilliseconds", "reason"
        },
        additionalProperties = false
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ApprovalTokenPattern();

    [GeneratedRegex("(?i)(?:api[-_]?key|token|secret|password|credential)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SensitiveOptionPattern();
}

internal sealed class McpToolInputException : Exception
{
    public McpToolInputException(string message)
        : base(message)
    {
    }
}

internal sealed class McpUnknownToolException : Exception
{
    public McpUnknownToolException(string name)
        : base($"Unknown tool: {name}")
    {
    }
}
