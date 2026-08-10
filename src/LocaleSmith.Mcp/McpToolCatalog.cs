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

    public McpToolCatalog(
        ISystemPromptContextProvider contextProvider,
        ICliCommandPolicy commandPolicy,
        ICliRunner? cliRunner,
        McpServerOptions options)
    {
        _contextProvider = contextProvider;
        _commandPolicy = commandPolicy;
        _cliRunner = cliRunner;
        _options = options;
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

        return new { tools };
    }

    public async Task<object> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        return name switch
        {
            "system.context" => await GetSystemContextAsync(arguments, cancellationToken).ConfigureAwait(false),
            "cli.propose" => ProposeCli(arguments),
            "cli.execute" when _options.EnableCliExecution =>
                await ExecuteCliAsync(arguments, cancellationToken).ConfigureAwait(false),
            _ => throw new McpUnknownToolException(name)
        };
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

    private static object EmptyObjectSchema() => new
    {
        type = "object",
        additionalProperties = false
    };

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
