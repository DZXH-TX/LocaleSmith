using System.Text;
using System.Text.Json;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Mcp.Tests;

public sealed class McpStdioServerTests
{
    private const string Initialize = """
        {"jsonrpc":"2.0","id":10,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """;
    private const string Initialized = """
        {"jsonrpc":"2.0","method":"notifications/initialized"}
        """;

    [Fact]
    public async Task InitializationSequenceIsRequiredBeforeTools()
    {
        using var server = CreateServer();
        var responses = await ExchangeAsync(
            server,
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            Initialize,
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            Initialized,
            """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""");

        Assert.Equal(4, responses.Count);
        Assert.Equal(-32002, ErrorCode(responses[0]));
        Assert.Equal(McpStdioServer.ProtocolVersion, responses[1].GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal(
            "LocaleSmith Local Tools",
            responses[1].GetProperty("result").GetProperty("serverInfo").GetProperty("title").GetString());
        Assert.Equal(-32002, ErrorCode(responses[2]));
        Assert.True(responses[3].GetProperty("result").GetProperty("tools").GetArrayLength() >= 2);
    }

    [Fact]
    public async Task ToolListDescribesReadOnlyAndHighRiskBoundaries()
    {
        using var server = CreateServer(enableCliExecution: true);
        var responses = await ExchangeAsync(
            server,
            Initialize,
            Initialized,
            """{"jsonrpc":"2.0","id":"list","method":"tools/list","params":{}}""");

        var tools = responses[1].GetProperty("result").GetProperty("tools");
        Assert.Equal(3, tools.GetArrayLength());
        var context = FindTool(tools, "system.context");
        var proposal = FindTool(tools, "cli.propose");
        var execution = FindTool(tools, "cli.execute");
        Assert.True(context.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.True(proposal.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.True(execution.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        Assert.False(execution.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal("forbidden", execution.GetProperty("execution").GetProperty("taskSupport").GetString());
    }

    [Fact]
    public async Task CliExecutionIsHiddenByDefault()
    {
        using var server = CreateServer();
        var responses = await ExchangeAsync(
            server,
            Initialize,
            Initialized,
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        var tools = responses[1].GetProperty("result").GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());
        Assert.DoesNotContain(tools.EnumerateArray(), static tool => tool.GetProperty("name").GetString() == "cli.execute");
    }

    [Fact]
    public async Task ProjectToolsRequireAnInjectedAppBackendAndRejectHostPaths()
    {
        var backend = new RecordingProjectBackend();
        using var server = new McpStdioServer(
            new FixedContextProvider("safe context"),
            new RecordingPolicy(CliPolicyDecision.Permit(@"C:\Program Files\dotnet\dotnet.exe")),
            projectBackend: backend);
        string projectId = backend.ProjectId.ToString("D");
        var responses = await ExchangeAsync(
            server,
            Initialize,
            Initialized,
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"archive.inspect","arguments":{"projectId":"__PROJECT__","sourcePath":"C:\\untrusted.jar"}}}
            """.Replace("__PROJECT__", projectId, StringComparison.Ordinal));

        JsonElement tools = responses[1].GetProperty("result").GetProperty("tools");
        Assert.Equal(7, tools.GetArrayLength());
        Assert.Contains(tools.EnumerateArray(), static tool =>
            tool.GetProperty("name").GetString() == "translation.start");
        Assert.True(responses[2].GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("Unknown tool argument", responses[2].GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, backend.InspectionCount);
    }

    [Fact]
    public async Task MissingUiApprovalTokenNeverReachesRunner()
    {
        var runner = new RecordingCliRunner();
        using var server = CreateServer(enableCliExecution: true, runner: runner);
        var responses = await ExchangeAsync(
            server,
            Initialize,
            Initialized,
            """
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"cli.execute","arguments":{"executable":"dotnet","arguments":["--info"],"workingDirectory":"C:\\sandbox","timeoutSeconds":30}}}
            """);

        var result = responses[1].GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(0, runner.CallCount);
        Assert.Contains(
            "approvalToken",
            result.GetProperty("content")[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidTokenShapeIsPassedToRunnerWithoutBeingEchoed()
    {
        var runner = new RecordingCliRunner();
        using var server = CreateServer(enableCliExecution: true, runner: runner);
        var token = new string('A', 43);
        var call = """
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"cli.execute","arguments":{"executable":"dotnet","workingDirectory":"C:\\sandbox","approvalToken":"__TOKEN__"}}}
            """.Replace("__TOKEN__", token, StringComparison.Ordinal);
        var responses = await ExchangeAsync(server, Initialize, Initialized, call);

        Assert.Equal(1, runner.CallCount);
        Assert.DoesNotContain(token, responses[1].GetRawText(), StringComparison.Ordinal);
        Assert.False(responses[1].GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task CliProposalOnlyEvaluatesPolicyAndReturnsApprovalSummary()
    {
        var policy = new RecordingPolicy(CliPolicyDecision.Permit(@"C:\Program Files\dotnet\dotnet.exe"));
        var runner = new RecordingCliRunner();
        using var server = CreateServer(enableCliExecution: true, runner: runner, policy: policy);
        var responses = await ExchangeAsync(
            server,
            Initialize,
            Initialized,
            """
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"cli.propose","arguments":{"executable":"dotnet","arguments":["--info"],"workingDirectory":"C:\\sandbox"}}}
            """);

        var structured = responses[1].GetProperty("result").GetProperty("structuredContent");
        Assert.True(structured.GetProperty("allowed").GetBoolean());
        Assert.True(structured.GetProperty("approval").GetProperty("required").GetBoolean());
        Assert.False(structured.GetProperty("approval").GetProperty("tokenIssued").GetBoolean());
        Assert.Equal(1, policy.EvaluationCount);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task InvalidAndUnknownRequestsReturnProtocolErrors()
    {
        using var server = CreateServer();
        var responses = await ExchangeAsync(
            server,
            "{\"jsonrpc\":",
            Initialize,
            Initialized,
            """{"jsonrpc":"2.0","id":1,"method":"unknown/method"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"missing.tool","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"system.context","arguments":[]}}""");

        Assert.Equal(-32700, ErrorCode(responses[0]));
        Assert.Equal(-32601, ErrorCode(responses[2]));
        Assert.Equal(-32602, ErrorCode(responses[3]));
        Assert.Equal(-32602, ErrorCode(responses[4]));
    }

    [Fact]
    public async Task InvalidToolArgumentsBecomeSafeToolErrors()
    {
        using var server = CreateServer();
        var responses = await ExchangeAsync(
            server,
            Initialize,
            Initialized,
            """
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"cli.propose","arguments":{"executable":"dotnet","workingDirectory":"C:\\sandbox","unexpected":true}}}
            """);

        var result = responses[1].GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("Unknown tool argument", result.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedLineIsDiscardedAndNextMessageStillWorks()
    {
        using var server = CreateServer(
            options: new McpServerOptions
            {
                MaximumMessageBytes = 256
            });
        var oversized = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"padding\":\"" + new string('x', 512) + "\"}";
        var responses = await ExchangeAsync(
            server,
            oversized,
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""");

        Assert.Equal(-32001, ErrorCode(responses[0]));
        Assert.Equal(2, responses[1].GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Object, responses[1].GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task OversizedToolResultIsReplacedWithBoundedProtocolError()
    {
        using var server = CreateServer(
            contextProvider: new FixedContextProvider(new string('x', 2048)),
            options: new McpServerOptions
            {
                MaximumMessageBytes = 1024,
                MaximumOutputCharacters = 1024
            });
        var responses = await ExchangeAsync(
            server,
            Initialize,
            Initialized,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"system.context","arguments":{}}}""");

        Assert.Equal(-32001, ErrorCode(responses[1]));
        Assert.Contains("message-size", responses[1].GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationNotificationCancelsInFlightToolCall()
    {
        using var server = CreateServer(contextProvider: new BlockingContextProvider());
        var responses = await ExchangeAsync(
            server,
            Initialize,
            Initialized,
            """{"jsonrpc":"2.0","id":"slow","method":"tools/call","params":{"name":"system.context","arguments":{}}}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"slow","reason":"test"}}""");

        Assert.Equal(-32800, ErrorCode(responses[1]));
    }

    [Fact]
    public async Task OutputIsStrippedOfAnsiControlsAndLikelySecrets()
    {
        var context = new FixedContextProvider("\u001b[31mTOKEN=top-secret\u001b[0m\u0000 context");
        using var server = CreateServer(contextProvider: context);
        var responses = await ExchangeAsync(
            server,
            Initialize,
            Initialized,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"system.context","arguments":{}}}""");

        var text = responses[1].GetRawText();
        Assert.DoesNotContain("top-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u001b", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REDACTED", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestRateLimitIsEnforced()
    {
        using var server = CreateServer(
            options: new McpServerOptions
            {
                MaximumRequestsPerWindow = 1,
                RateLimitWindow = TimeSpan.FromMinutes(1)
            });
        var responses = await ExchangeAsync(
            server,
            Initialize,
            Initialized,
            """{"jsonrpc":"2.0","id":1,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""");

        Assert.Equal(JsonValueKind.Object, responses[1].GetProperty("result").ValueKind);
        Assert.Equal(-32029, ErrorCode(responses[2]));
    }

    private static McpStdioServer CreateServer(
        bool enableCliExecution = false,
        ICliRunner? runner = null,
        ICliCommandPolicy? policy = null,
        ISystemPromptContextProvider? contextProvider = null,
        McpServerOptions? options = null)
    {
        options ??= new McpServerOptions { EnableCliExecution = enableCliExecution };
        runner ??= enableCliExecution ? new RecordingCliRunner() : null;
        return new McpStdioServer(
            contextProvider ?? new FixedContextProvider("safe context"),
            policy ?? new RecordingPolicy(CliPolicyDecision.Permit(@"C:\Program Files\dotnet\dotnet.exe")),
            runner,
            options);
    }

    private static async Task<IReadOnlyList<JsonElement>> ExchangeAsync(
        McpStdioServer server,
        params string[] messages)
    {
        var payload = string.Join('\n', messages) + "\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var output = new MemoryStream();
        await server.RunAsync(input, output, TestContext.Current.CancellationToken);
        var lines = Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Select(static line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();
    }

    private static int ErrorCode(JsonElement response) =>
        response.GetProperty("error").GetProperty("code").GetInt32();

    private static JsonElement FindTool(JsonElement tools, string name) =>
        tools.EnumerateArray().Single(tool => string.Equals(
            tool.GetProperty("name").GetString(),
            name,
            StringComparison.Ordinal));

    private sealed class FixedContextProvider(string value) : ISystemPromptContextProvider
    {
        public ValueTask<string> BuildAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(value);
        }
    }

    private sealed class BlockingContextProvider : ISystemPromptContextProvider
    {
        public async ValueTask<string> BuildAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return string.Empty;
        }
    }

    private sealed class RecordingPolicy(CliPolicyDecision decision) : ICliCommandPolicy
    {
        public int EvaluationCount { get; private set; }

        public IReadOnlySet<string> AllowedExecutables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void ReplaceAllowedExecutables(IEnumerable<string> executables)
        {
        }

        public bool AddAllowedExecutable(string executable) => false;

        public bool RemoveAllowedExecutable(string executable) => false;

        public CliPolicyDecision Evaluate(CliCommand command)
        {
            EvaluationCount++;
            return decision;
        }
    }

    private sealed class RecordingCliRunner : ICliRunner
    {
        public int CallCount { get; private set; }

        public Task<CliExecutionResult> ExecuteAsync(
            CliCommand command,
            string approvalToken,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(
                new CliExecutionResult(
                    CliExecutionStatus.Completed,
                    0,
                    "done",
                    string.Empty,
                    TimeSpan.FromMilliseconds(10)));
        }
    }

    private sealed class RecordingProjectBackend : IProjectMcpBackend
    {
        public Guid ProjectId { get; } = Guid.NewGuid();

        public int InspectionCount { get; private set; }

        public ValueTask<ProjectMcpSnapshot?> GetActiveProjectAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ProjectMcpSnapshot?>(
                new ProjectMcpSnapshot(ProjectId, "example.jar", null, null, null, null));

        public ValueTask<ProjectMcpSnapshot?> GetProjectAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ProjectMcpSnapshot?>(
                projectId == ProjectId
                    ? new ProjectMcpSnapshot(ProjectId, "example.jar", null, null, null, null)
                    : null);

        public ValueTask<ArchiveMcpInspection> InspectArchiveAsync(
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            InspectionCount++;
            return ValueTask.FromResult(new ArchiveMcpInspection(
                projectId,
                "example.jar",
                "example",
                "Fabric",
                1,
                0,
                "none",
                false,
                []));
        }

        public ValueTask<TaskMcpSnapshot> StartTranslationAsync(
            TranslationMcpStartRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<TaskMcpSnapshot?> GetTaskAsync(
            Guid taskId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<TaskMcpSnapshot?>(null);

        public ValueTask<TaskMcpSnapshot?> GetTaskAsync(
            Guid projectId,
            Guid taskId,
            CancellationToken cancellationToken = default) =>
            GetTaskAsync(taskId, cancellationToken);

        public ValueTask<TaskMcpSnapshot> CancelTaskAsync(
            Guid taskId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<TaskMcpSnapshot> CancelTaskAsync(
            Guid projectId,
            Guid taskId,
            CancellationToken cancellationToken = default) =>
            CancelTaskAsync(taskId, cancellationToken);
    }
}
