using System.Net;
using System.Text;
using System.Text.Json;
using LocaleSmith.App.Services;
using LocaleSmith.Application.Models;
using LocaleSmith.Application.Services;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;
using LocaleSmith.Infrastructure.Models;
using LocaleSmith.Infrastructure.Security;
using LocaleSmith.Mcp;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.Services;

namespace LocaleSmith.App.Tests;

public sealed class ModelAssistantServiceTests
{
    [Fact]
    public async Task InjectsSanitizedContextAndReturnsUiReviewableCliProposal()
    {
        using var arguments = JsonDocument.Parse(
            """{"executable":"dotnet","arguments":["--info"],"workingDirectory":"C:\\sandbox","timeoutSeconds":15}""");
        var model = new SequenceModelService(
            new ModelResponse(
                string.Empty,
                toolCalls: [new ModelToolCall("call-1", "cli_propose", arguments.RootElement)]),
            new ModelResponse("The proposal is ready for your review."));
        using var registry = new ModelServiceRegistry();
        registry.AddOrUpdate(model);
        var policy = new PermitPolicy();
        var context = new FixedContextProvider("Windows 11; shell=PowerShell 7.6; USERPROFILE=[redacted]");
        var bridge = new McpModelToolExecutor(context, policy);
        var service = new ModelAssistantService(
            registry,
            context,
            new FixedConfigurationService(@"C:\sandbox"),
            bridge,
            new ModelToolOrchestrator());

        var completion = await service.CompleteAsync(
            model.Source.Id,
            [new ModelMessage(ModelMessageRole.User, "Prepare a dotnet diagnostic command.")],
            TestContext.Current.CancellationToken);

        Assert.Equal("The proposal is ready for your review.", completion.Content);
        CliCommand command = Assert.Single(completion.ProposedCommands);
        Assert.Equal("dotnet", command.Executable);
        Assert.Equal(["--info"], command.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(15), command.Timeout);
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("LocaleSmith (译匠)", model.Requests[0].Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("PowerShell 7.6", model.Requests[0].Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains(@"C:\sandbox", model.Requests[0].Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains(model.Requests[0].Tools, static tool => tool.Name == "system_context");
        Assert.Contains(model.Requests[0].Tools, static tool => tool.Name == "cli_propose");
        Assert.DoesNotContain(model.Requests[0].Tools, static tool => tool.Name.Contains("execute", StringComparison.Ordinal));
        Assert.Equal(ModelMessageRole.Tool, model.Requests[1].Messages[^1].Role);
        Assert.Equal(1, policy.EvaluationCount);
    }

    [Fact]
    public async Task KimiToolRoundReplaysReasoningContentOnTheWireWithoutDisplayingIt()
    {
        const string reasoningContent = "\n  opaque \"trace\" 保留原样  \n";
        string firstResponse = JsonSerializer.Serialize(new
        {
            model = "kimi-k3",
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = (string?)null,
                        reasoning_content = reasoningContent,
                        tool_calls = new[]
                        {
                            new
                            {
                                id = "call-1",
                                type = "function",
                                function = new { name = "system_context", arguments = "{}" }
                            }
                        }
                    }
                }
            }
        });
        string secondResponse = JsonSerializer.Serialize(new
        {
            model = "kimi-k3",
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = "Visible final answer.",
                        reasoning_content = "private final reasoning"
                    }
                }
            }
        });
        var requestBodies = new List<string>();
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return JsonResponse(requestBodies.Count == 1 ? firstResponse : secondResponse);
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/kimi", "secret".AsMemory(), TestContext.Current.CancellationToken);
        var source = new ModelSource(
            "kimi",
            "Kimi",
            ModelProviderKind.OpenAiCompatible,
            ModelProviderPresets.Kimi.DefaultEndpoint!,
            ModelProviderPresets.Kimi.DefaultModelName!,
            "providers/kimi",
            ModelProviderPresets.KimiId);
        var model = new OpenAiCompatibleModelService(client, source, secrets);
        using var registry = new ModelServiceRegistry();
        registry.AddOrUpdate(model);
        var context = new FixedContextProvider("Windows 11; shell=PowerShell 7.6");
        var assistant = new ModelAssistantService(
            registry,
            context,
            new FixedConfigurationService(@"C:\sandbox"),
            new McpModelToolExecutor(context, new PermitPolicy()),
            new ModelToolOrchestrator());

        ModelAssistantCompletion completion = await assistant.CompleteAsync(
            source.Id,
            [new ModelMessage(ModelMessageRole.User, "Read the safe system context.")],
            TestContext.Current.CancellationToken);

        Assert.Equal("Visible final answer.", completion.Content);
        Assert.DoesNotContain(reasoningContent, completion.Content, StringComparison.Ordinal);
        Assert.Equal(2, requestBodies.Count);
        using var secondRequest = JsonDocument.Parse(requestBodies[1]);
        JsonElement replayedAssistant = secondRequest.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Single(static message =>
                message.GetProperty("role").GetString() == "assistant" &&
                message.TryGetProperty("tool_calls", out _));
        Assert.Equal(reasoningContent, replayedAssistant.GetProperty("reasoning_content").GetString());
        Assert.Equal(JsonValueKind.Null, replayedAssistant.GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task MissingOrChangedModelSourceFailsBeforeSending()
    {
        using var registry = new ModelServiceRegistry();
        var context = new FixedContextProvider("Windows");
        var bridge = new McpModelToolExecutor(context, new PermitPolicy());
        var service = new ModelAssistantService(
            registry,
            context,
            new FixedConfigurationService(@"C:\sandbox"),
            bridge,
            new ModelToolOrchestrator());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteAsync(
                "removed",
                [new ModelMessage(ModelMessageRole.User, "hello")],
                TestContext.Current.CancellationToken));

        Assert.Contains("no longer available", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectContextPublicEventsAndProviderUsageReachTheUiContract()
    {
        var model = new SequenceModelService(new ModelResponse(
            "Project answer.",
            model: "provider-model",
            inputTokens: 90,
            outputTokens: 10,
            totalTokens: 100));
        using var registry = new ModelServiceRegistry();
        registry.AddOrUpdate(model);
        var context = new FixedContextProvider("Windows");
        var workspace = new InMemoryModProjectWorkspace();
        ModProjectSnapshot project = workspace.RegisterProject(Path.Combine(Path.GetTempPath(), "project-mod.jar"));
        _ = workspace.RegisterTask(
            project.ProjectId,
            new ModProjectTaskRegistration(
                project.SourceArtifactPath,
                Path.Combine(Path.GetTempPath(), "project-mod-output.jar"),
                model.Source.Id,
                "zh_cn",
                TranslationStyle.Formal,
                "Translate the active mod and preserve placeholders."));
        project = workspace.ActiveProject!;
        var events = new RecordingProgress<ModelRunEvent>();
        var assistant = new ModelAssistantService(
            registry,
            context,
            new FixedConfigurationService(@"C:\sandbox"),
            new McpModelToolExecutor(context, new PermitPolicy()),
            new ModelToolOrchestrator());

        ModelAssistantCompletion completion = await assistant.CompleteAsync(
            model.Source.Id,
            [new ModelMessage(ModelMessageRole.User, "Work on this project.")],
            project,
            events,
            TestContext.Current.CancellationToken);

        Assert.Equal(100, completion.ModelUsage?.TotalTokens);
        Assert.True(completion.ModelUsage?.IsComplete);
        Assert.Equal("provider-model", completion.Model);
        Assert.Equal(
            [
                ModelRunEventKind.ModelRoundStarted,
                ModelRunEventKind.ModelRoundCompleted,
                ModelRunEventKind.RunCompleted
            ],
            events.Values.Select(static item => item.Kind));
        string systemPrompt = Assert.Single(model.Requests).Messages[0].Content;
        Assert.Contains(project.ProjectId.ToString("D"), systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Translate the active mod", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(project.SourceArtifactPath, systemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProjectMutationToolsAreUnavailableWithoutOneTurnAuthorization()
    {
        var backend = new RecordingProjectBackend();
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            projectId = backend.ProjectId,
            objective = "Translate now"
        }));
        var model = new SequenceModelService(
            new ModelResponse(
                string.Empty,
                toolCalls: [new ModelToolCall("call-1", "translation_start", arguments.RootElement)]),
            new ModelResponse("No mutation ran."));
        using var registry = new ModelServiceRegistry();
        registry.AddOrUpdate(model);
        var context = new FixedContextProvider("Windows");
        var assistant = new ModelAssistantService(
            registry,
            context,
            new FixedConfigurationService(@"C:\sandbox"),
            new McpModelToolExecutor(context, new PermitPolicy(), projectBackend: backend),
            new ModelToolOrchestrator());
        ModProjectSnapshot project = CreateProject(backend.ProjectId);

        ModelAssistantCompletion completion = await assistant.CompleteAsync(
            model.Source.Id,
            [new ModelMessage(ModelMessageRole.User, "Describe the project only.")],
            project,
            progress: null,
            allowProjectChanges: false,
            TestContext.Current.CancellationToken);

        Assert.Equal("No mutation ran.", completion.Content);
        Assert.Equal(0, backend.StartCount);
        Assert.DoesNotContain(model.Requests[0].Tools, static tool => tool.Name == "translation_start");
        Assert.DoesNotContain(model.Requests[0].Tools, static tool => tool.Name == "task_cancel");
        Assert.Contains(model.Requests[0].Tools, static tool => tool.Name == "archive_inspect");
    }

    [Fact]
    public async Task AuthorizedTranslationBindsCapturedProjectAndAssistantModelSource()
    {
        var backend = new RecordingProjectBackend();
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            projectId = Guid.NewGuid(),
            objective = "Translate the selected project",
            modelSourceId = "attacker-selected-source",
            targetLanguage = "ja_jp",
            style = "informal"
        }));
        var model = new SequenceModelService(
            new ModelResponse(
                string.Empty,
                toolCalls: [new ModelToolCall("call-1", "translation_start", arguments.RootElement)]),
            new ModelResponse("Translation accepted."));
        using var registry = new ModelServiceRegistry();
        registry.AddOrUpdate(model);
        var context = new FixedContextProvider("Windows");
        var assistant = new ModelAssistantService(
            registry,
            context,
            new FixedConfigurationService(@"C:\sandbox"),
            new McpModelToolExecutor(context, new PermitPolicy(), projectBackend: backend),
            new ModelToolOrchestrator());

        await assistant.CompleteAsync(
            model.Source.Id,
            [new ModelMessage(ModelMessageRole.User, "Start this translation.")],
            CreateProject(backend.ProjectId),
            progress: null,
            allowProjectChanges: true,
            TestContext.Current.CancellationToken);

        TranslationMcpStartRequest request = Assert.IsType<TranslationMcpStartRequest>(backend.LastStartRequest);
        Assert.Equal(1, backend.StartCount);
        Assert.Equal(backend.ProjectId, request.ProjectId);
        Assert.Equal(model.Source.Id, request.ModelSourceId);
        Assert.Equal("ja_jp", request.TargetLanguage);
        Assert.Equal("informal", request.Style);
    }

    [Fact]
    public async Task ProjectToolsRemainBoundWhenGlobalActiveProjectChangesMidTurn()
    {
        var backend = new RecordingProjectBackend
        {
            ActiveProjectId = Guid.NewGuid()
        };
        using var emptyArguments = JsonDocument.Parse("{}");
        using var taskArguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            taskId = Guid.NewGuid()
        }));
        var model = new SequenceModelService(
            new ModelResponse(
                string.Empty,
                toolCalls: [new ModelToolCall("call-1", "project_get_active", emptyArguments.RootElement)]),
            new ModelResponse(
                string.Empty,
                toolCalls: [new ModelToolCall("call-2", "task_cancel", taskArguments.RootElement)]),
            new ModelResponse("Scoped operations complete."));
        using var registry = new ModelServiceRegistry();
        registry.AddOrUpdate(model);
        var context = new FixedContextProvider("Windows");
        var assistant = new ModelAssistantService(
            registry,
            context,
            new FixedConfigurationService(@"C:\sandbox"),
            new McpModelToolExecutor(context, new PermitPolicy(), projectBackend: backend),
            new ModelToolOrchestrator());

        await assistant.CompleteAsync(
            model.Source.Id,
            [new ModelMessage(ModelMessageRole.User, "Cancel the captured project's task.")],
            CreateProject(backend.ProjectId),
            progress: null,
            allowProjectChanges: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(backend.ProjectId, backend.ScopedCancelProjectId);
        Assert.Equal(0, backend.LegacyCancelCount);
        string projectToolResult = model.Requests[1].Messages[^1].Content;
        Assert.Contains(backend.ProjectId.ToString("D"), projectToolResult, StringComparison.Ordinal);
        Assert.DoesNotContain(backend.ActiveProjectId.ToString("D"), projectToolResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversationBeyondFormerCountAndCharacterLimitsIsForwardedInFull()
    {
        var model = new SequenceModelService(new ModelResponse("accepted"));
        using var registry = new ModelServiceRegistry();
        registry.AddOrUpdate(model);
        var context = new FixedContextProvider("Windows");
        var service = new ModelAssistantService(
            registry,
            context,
            new FixedConfigurationService(@"C:\sandbox"),
            new McpModelToolExecutor(context, new PermitPolicy()),
            new ModelToolOrchestrator());
        var conversation = Enumerable.Range(0, 41)
            .Select(index => new ModelMessage(
                ModelMessageRole.User,
                index == 0 ? new string('x', (256 * 1024) + 1) : $"message-{index}"))
            .ToArray();

        ModelAssistantCompletion completion = await service.CompleteAsync(
            model.Source.Id,
            conversation,
            TestContext.Current.CancellationToken);

        Assert.Equal("accepted", completion.Content);
        ModelRequest request = Assert.Single(model.Requests);
        Assert.Equal(conversation.Length + 1, request.Messages.Count);
        Assert.Equal(conversation, request.Messages.Skip(1));
    }

    [Fact]
    public async Task EmptyOrNonPlainUiConversationIsRejectedBeforeProviderCall()
    {
        var model = new SequenceModelService(new ModelResponse("unused"));
        using var registry = new ModelServiceRegistry();
        registry.AddOrUpdate(model);
        var context = new FixedContextProvider("Windows");
        var service = new ModelAssistantService(
            registry,
            context,
            new FixedConfigurationService(@"C:\sandbox"),
            new McpModelToolExecutor(context, new PermitPolicy()),
            new ModelToolOrchestrator());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CompleteAsync(
            model.Source.Id,
            [],
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteAsync(
            model.Source.Id,
            [new ModelMessage(ModelMessageRole.System, "not UI history")],
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteAsync(
            model.Source.Id,
            [null!],
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteAsync(
            model.Source.Id,
            [new ModelMessage(ModelMessageRole.Assistant, "visible", reasoningContent: "private protocol state")],
            TestContext.Current.CancellationToken));

        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task PolicyRejectedCliProposalIsNotSurfacedForConfirmation()
    {
        using var arguments = JsonDocument.Parse(
            """{"executable":"dotnet","arguments":["--info"],"workingDirectory":"C:\\sandbox"}""");
        var model = new SequenceModelService(
            new ModelResponse(
                string.Empty,
                toolCalls: [new ModelToolCall("call-1", "cli_propose", arguments.RootElement)]),
            new ModelResponse("The unsafe proposal was rejected."));
        using var registry = new ModelServiceRegistry();
        registry.AddOrUpdate(model);
        var context = new FixedContextProvider("Windows");
        var bridge = new McpModelToolExecutor(context, new PermitPolicy(permit: false));
        var service = new ModelAssistantService(
            registry,
            context,
            new FixedConfigurationService(@"C:\sandbox"),
            bridge,
            new ModelToolOrchestrator());

        ModelAssistantCompletion completion = await service.CompleteAsync(
            model.Source.Id,
            [new ModelMessage(ModelMessageRole.User, "Prepare a command.")],
            TestContext.Current.CancellationToken);

        Assert.Empty(completion.ProposedCommands);
    }

    private sealed class SequenceModelService(params ModelResponse[] responses) : IModelService
    {
        private readonly Queue<ModelResponse> _responses = new(responses);

        public ModelSource Source { get; } = new(
            "assistant-model",
            "Assistant model",
            ModelProviderKind.Ollama,
            new Uri("http://127.0.0.1:11434"),
            "qwen3");

        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class FixedContextProvider(string value) : ISystemPromptContextProvider
    {
        public ValueTask<string> BuildAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(value);
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private static ModProjectSnapshot CreateProject(Guid projectId) => new(
        projectId,
        Path.Combine(Path.GetTempPath(), "scoped-project.jar"),
        "scoped-project",
        "Fabric",
        [],
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private sealed class RecordingProjectBackend : IProjectMcpBackend
    {
        public Guid ProjectId { get; } = Guid.NewGuid();

        public Guid ActiveProjectId { get; init; }

        public int StartCount { get; private set; }

        public int LegacyCancelCount { get; private set; }

        public Guid? ScopedCancelProjectId { get; private set; }

        public TranslationMcpStartRequest? LastStartRequest { get; private set; }

        public ValueTask<ProjectMcpSnapshot?> GetActiveProjectAsync(
            CancellationToken cancellationToken = default)
        {
            Guid activeProjectId = ActiveProjectId == Guid.Empty ? ProjectId : ActiveProjectId;
            return ValueTask.FromResult<ProjectMcpSnapshot?>(new ProjectMcpSnapshot(
                activeProjectId,
                "global-active.jar",
                "global-active",
                "Fabric",
                null,
                null));
        }

        public ValueTask<ProjectMcpSnapshot?> GetProjectAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ProjectMcpSnapshot?>(projectId == ProjectId
                ? new ProjectMcpSnapshot(ProjectId, "scoped-project.jar", "scoped-project", "Fabric", null, null)
                : null);

        public ValueTask<ArchiveMcpInspection> InspectArchiveAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ArchiveMcpInspection(
                projectId,
                "scoped-project.jar",
                "scoped-project",
                "Fabric",
                1,
                1,
                "none",
                false,
                []));

        public ValueTask<TaskMcpSnapshot> StartTranslationAsync(
            TranslationMcpStartRequest request,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            LastStartRequest = request;
            return ValueTask.FromResult(CreateTask(request.ProjectId, request.Objective));
        }

        public ValueTask<TaskMcpSnapshot?> GetTaskAsync(
            Guid taskId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<TaskMcpSnapshot?>(CreateTask(ProjectId, "status"));

        public ValueTask<TaskMcpSnapshot?> GetTaskAsync(
            Guid projectId,
            Guid taskId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<TaskMcpSnapshot?>(projectId == ProjectId
                ? CreateTask(projectId, "status")
                : null);

        public ValueTask<TaskMcpSnapshot> CancelTaskAsync(
            Guid taskId,
            CancellationToken cancellationToken = default)
        {
            LegacyCancelCount++;
            return ValueTask.FromResult(CreateTask(ActiveProjectId, "cancel"));
        }

        public ValueTask<TaskMcpSnapshot> CancelTaskAsync(
            Guid projectId,
            Guid taskId,
            CancellationToken cancellationToken = default)
        {
            ScopedCancelProjectId = projectId;
            return ValueTask.FromResult(CreateTask(projectId, "cancel"));
        }

        private static TaskMcpSnapshot CreateTask(Guid projectId, string objective) => new(
            Guid.NewGuid(),
            projectId,
            Guid.NewGuid(),
            objective,
            "assistant-model",
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

    private sealed class FixedConfigurationService(string sandboxPath) : IAppConfigurationService
    {
        public Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AppConfiguration
            {
                IsOnboardingComplete = true,
                WorkspacePath = Path.Combine(Path.GetTempPath(), "LocaleSmith", "Workspace"),
                SandboxPath = sandboxPath
            });
        }

        public Task SaveAsync(
            AppConfiguration configuration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveSettingsAsync(
            AppSettingsUpdate settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class PermitPolicy(bool permit = true) : ICliCommandPolicy
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
            return permit
                ? CliPolicyDecision.Permit(@"C:\Program Files\dotnet\dotnet.exe")
                : CliPolicyDecision.Deny(CliPolicyViolation.PathArgumentOutsideSandbox, "Rejected by test policy.");
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
