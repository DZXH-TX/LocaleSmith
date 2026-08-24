using System.Text.Json;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Mcp.Tests;

public sealed class McpModelToolExecutorTests
{
    [Fact]
    public void ExposesOnlyProviderSafeReadAndProposalTools()
    {
        var executor = CreateExecutor(out _);

        Assert.Equal(["cli_propose", "system_context"], executor.Tools.Select(static tool => tool.Name).Order());
        Assert.DoesNotContain(executor.Tools, static tool => tool.Name.Contains("execute", StringComparison.Ordinal));
        Assert.All(executor.Tools, static tool => Assert.DoesNotContain('.', tool.Name));
    }

    [Fact]
    public async Task SystemContextIsSanitizedAndBounded()
    {
        var executor = CreateExecutor(out _, "\u001b[31mTOKEN=top-secret\u001b[0m\u0000 safe");
        ModelToolCall call = CreateCall("call-1", "system_context", "{}");

        ModelToolResult result = await executor.ExecuteAsync(call, TestContext.Current.CancellationToken);

        Assert.Equal("call-1", result.ToolCallId);
        Assert.DoesNotContain("top-secret", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", result.Content, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliProposalEvaluatesPolicyButCannotExecuteOrIssueToken()
    {
        var executor = CreateExecutor(out RecordingPolicy policy);
        ModelToolCall call = CreateCall(
            "call-2",
            "cli_propose",
            """{"executable":"dotnet","arguments":["--info"],"workingDirectory":"C:\\sandbox"}""");

        ModelToolResult result = await executor.ExecuteAsync(call, TestContext.Current.CancellationToken);

        Assert.Equal(1, policy.EvaluationCount);
        Assert.Contains("\"allowed\":true", result.Content, StringComparison.Ordinal);
        Assert.Contains("\"tokenIssued\":false", result.Content, StringComparison.Ordinal);
        Assert.Contains("nothing was executed", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownProviderToolFailsClosed()
    {
        var executor = CreateExecutor(out _);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(
                CreateCall("call-3", "cli_execute", "{}"),
                TestContext.Current.CancellationToken));

        Assert.Contains("not exposed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectToolsAreConditionallyExposedWithProviderSafeAliases()
    {
        var backend = new RecordingProjectBackend();
        var executor = CreateExecutor(out _, projectBackend: backend);

        Assert.Equal(
            [
                "archive_inspect",
                "cli_propose",
                "project_get_active",
                "system_context",
                "task_cancel",
                "task_status",
                "translation_start"
            ],
            executor.Tools.Select(static tool => tool.Name).Order());
        Assert.All(executor.Tools, static tool => Assert.DoesNotContain('.', tool.Name));
        ModelToolDefinition start = executor.Tools.Single(static tool => tool.Name == "translation_start");
        Assert.False(start.InputSchema.GetProperty("properties").TryGetProperty("sourcePath", out _));
    }

    [Fact]
    public async Task TranslationAliasRoutesOnlyOpaqueProjectDataToBackend()
    {
        var backend = new RecordingProjectBackend();
        var executor = CreateExecutor(out _, projectBackend: backend);
        string arguments = $$"""
            {"projectId":"{{backend.ProjectId:D}}","objective":"Translate the selected mod.","targetLanguage":"ja_JP"}
            """;

        ModelToolResult result = await executor.ExecuteAsync(
            CreateCall("call-project", "translation_start", arguments),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Equal(backend.ProjectId, backend.LastStartRequest?.ProjectId);
        Assert.Equal("Translate the selected mod.", backend.LastStartRequest?.Objective);
        Assert.Equal("ja_JP", backend.LastStartRequest?.TargetLanguage);
        Assert.DoesNotContain("sourcePath", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"taskId\"", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TaskId\"", result.Content, StringComparison.Ordinal);
    }

    private static McpModelToolExecutor CreateExecutor(
        out RecordingPolicy policy,
        string context = "Windows 11; PowerShell 7",
        IProjectMcpBackend? projectBackend = null)
    {
        policy = new RecordingPolicy();
        return new McpModelToolExecutor(
            new FixedContextProvider(context),
            policy,
            projectBackend: projectBackend);
    }

    private static ModelToolCall CreateCall(string id, string name, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new ModelToolCall(id, name, document.RootElement);
    }

    private sealed class FixedContextProvider(string context) : ISystemPromptContextProvider
    {
        public ValueTask<string> BuildAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(context);
        }
    }

    private sealed class RecordingPolicy : ICliCommandPolicy
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
            return CliPolicyDecision.Permit(@"C:\Program Files\dotnet\dotnet.exe");
        }
    }

    private sealed class RecordingProjectBackend : IProjectMcpBackend
    {
        public Guid ProjectId { get; } = Guid.NewGuid();

        public TranslationMcpStartRequest? LastStartRequest { get; private set; }

        public ValueTask<ProjectMcpSnapshot?> GetActiveProjectAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ProjectMcpSnapshot?>(
                new ProjectMcpSnapshot(ProjectId, "example.jar", "example", "Fabric", null, null));

        public ValueTask<ProjectMcpSnapshot?> GetProjectAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ProjectMcpSnapshot?>(
                projectId == ProjectId
                    ? new ProjectMcpSnapshot(ProjectId, "example.jar", "example", "Fabric", null, null)
                    : null);

        public ValueTask<ArchiveMcpInspection> InspectArchiveAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ArchiveMcpInspection(
                projectId,
                "example.jar",
                "example",
                "Fabric",
                5,
                1,
                "none",
                false,
                []));

        public ValueTask<TaskMcpSnapshot> StartTranslationAsync(
            TranslationMcpStartRequest request,
            CancellationToken cancellationToken = default)
        {
            LastStartRequest = request;
            return ValueTask.FromResult(CreateTask(request.ProjectId, request.Objective));
        }

        public ValueTask<TaskMcpSnapshot?> GetTaskAsync(
            Guid taskId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<TaskMcpSnapshot?>(CreateTask(ProjectId, "Translate"));

        public ValueTask<TaskMcpSnapshot?> GetTaskAsync(
            Guid projectId,
            Guid taskId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<TaskMcpSnapshot?>(CreateTask(projectId, "Translate"));

        public ValueTask<TaskMcpSnapshot> CancelTaskAsync(
            Guid taskId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateTask(ProjectId, "Translate"));

        public ValueTask<TaskMcpSnapshot> CancelTaskAsync(
            Guid projectId,
            Guid taskId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateTask(projectId, "Translate"));

        private static TaskMcpSnapshot CreateTask(Guid projectId, string objective) => new(
            Guid.NewGuid(),
            projectId,
            Guid.NewGuid(),
            objective,
            "source",
            "zh_CN",
            "Formal",
            "Queued",
            0,
            "Queued",
            null,
            null,
            [],
            null,
            null,
            null,
            null,
            0,
            false);
    }
}
