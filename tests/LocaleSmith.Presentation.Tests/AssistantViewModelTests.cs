using LocaleSmith.Application.Models;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.Services;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.Presentation.Tests;

public sealed class AssistantViewModelTests
{
    [Fact]
    public async Task CapturesModelPerSendAndRaisesSeparateCliReviewRequest()
    {
        var command = new CliCommand("dotnet", ["--info"], @"C:\sandbox");
        var assistant = new RecordingAssistantService(
            new ModelAssistantCompletion("Review this proposal.", [command]));
        var selection = new StubSelectionService(CreateSource("first"), CreateSource("second"));
        using var viewModel = new AssistantViewModel(assistant, selection);
        viewModel.RefreshModelSources();
        viewModel.SelectedModelSource = viewModel.ModelSources.Single(source => source.Id == "second");
        viewModel.Draft = "Prepare diagnostics";
        IReadOnlyList<CliCommand>? proposals = null;
        viewModel.CliProposalsRequested += (_, args) => proposals = args.Commands;

        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Equal("second", assistant.LastSourceId);
        Assert.Equal("Prepare diagnostics", Assert.Single(assistant.LastConversation!).Content);
        Assert.Equal(2, viewModel.Messages.Count);
        Assert.Equal("Review this proposal.", viewModel.Messages[1].Content);
        Assert.Same(command, Assert.Single(proposals!));
        Assert.Contains("separate review", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CliProposalRemainsClaimableWhenPageWasNotSubscribedAtCompletion()
    {
        var command = new CliCommand("dotnet", ["--info"], @"C:\sandbox");
        using var viewModel = new AssistantViewModel(
            new RecordingAssistantService(new ModelAssistantCompletion("Review later.", [command])),
            new StubSelectionService(CreateSource("local")));
        viewModel.RefreshModelSources();
        viewModel.Draft = "Prepare later review";

        await viewModel.SendCommand.ExecuteAsync(null);
        IReadOnlyList<CliCommand>? claimed = null;
        viewModel.CliProposalsRequested += (_, args) => claimed = args.Commands;
        viewModel.PublishPendingCliProposals();

        Assert.Same(command, Assert.Single(claimed!));
        claimed = null;
        viewModel.PublishPendingCliProposals();
        Assert.Null(claimed);
    }

    [Fact]
    public void MissingModelDisablesSendAndShowsActionableError()
    {
        using var viewModel = new AssistantViewModel(
            new RecordingAssistantService(new ModelAssistantCompletion("unused", [])),
            new StubSelectionService());

        viewModel.RefreshModelSources();
        viewModel.Draft = "hello";

        Assert.False(viewModel.SendCommand.CanExecute(null));
        Assert.Contains("Configure a model", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelStopsInFlightRequestWithoutAddingAssistantMessage()
    {
        var assistant = new BlockingAssistantService();
        using var viewModel = new AssistantViewModel(
            assistant,
            new StubSelectionService(CreateSource("local")));
        viewModel.RefreshModelSources();
        viewModel.Draft = "wait";

        Task send = viewModel.SendCommand.ExecuteAsync(null);
        await assistant.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        viewModel.CancelCommand.Execute(null);
        await send;

        Assert.Empty(viewModel.Messages);
        Assert.Equal("wait", viewModel.Draft);
        Assert.Contains("cancelled", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task LongConversationRetainsEveryCompleteTurnForTheProvider()
    {
        var assistant = new RecordingAssistantService(new ModelAssistantCompletion("answer", []));
        using var viewModel = new AssistantViewModel(
            assistant,
            new StubSelectionService(CreateSource("local")));
        viewModel.RefreshModelSources();

        for (var index = 0; index < 24; index++)
        {
            viewModel.Draft = $"question-{index}";
            await viewModel.SendCommand.ExecuteAsync(null);
            Assert.Equal((index * 2) + 1, assistant.LastConversation!.Count);
        }

        Assert.Equal(48, viewModel.Messages.Count);
        Assert.Equal("question-0", viewModel.Messages[0].Content);
        Assert.Equal("question-23", assistant.LastConversation![^1].Content);
    }

    [Fact]
    public async Task DraftAndHistoryBeyondFormerCharacterLimitsAreSentInFull()
    {
        string largeResponse = new('x', (256 * 1024) + 1);
        var assistant = new SequenceAssistantService(
            new ModelAssistantCompletion(largeResponse, []),
            new ModelAssistantCompletion("next answer", []));
        using var viewModel = new AssistantViewModel(
            assistant,
            new StubSelectionService(CreateSource("local")));
        viewModel.RefreshModelSources();
        viewModel.Draft = "seed";
        await viewModel.SendCommand.ExecuteAsync(null);
        Assert.Equal(2, viewModel.Messages.Count);

        string nextQuestion = $" {new string('y', (16 * 1024) + 1)} ";
        viewModel.Draft = nextQuestion;
        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Collection(
            assistant.LastConversation!,
            message => Assert.Equal("seed", message.Content),
            message => Assert.Equal(largeResponse, message.Content),
            message => Assert.Equal(nextQuestion, message.Content));
        Assert.Collection(
            viewModel.Messages,
            message => Assert.Equal("seed", message.Content),
            message => Assert.Equal(largeResponse, message.Content),
            message => Assert.Equal(nextQuestion, message.Content),
            message => Assert.Equal("next answer", message.Content));
    }

    [Fact]
    public async Task SwitchingModelSourcePreservesIndependentConversationWithoutCrossProviderDisclosure()
    {
        var assistant = new RecordingAssistantService(new ModelAssistantCompletion("answer", []));
        using var viewModel = new AssistantViewModel(
            assistant,
            new StubSelectionService(CreateSource("local"), CreateSource("cloud")));
        viewModel.RefreshModelSources();
        viewModel.Draft = "local-only context";
        await viewModel.SendCommand.ExecuteAsync(null);
        Assert.Equal(2, viewModel.Messages.Count);

        viewModel.SelectedModelSource = viewModel.ModelSources.Single(source => source.Id == "cloud");

        Assert.Empty(viewModel.Messages);
        Assert.Contains("independent conversation", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        viewModel.Draft = "cloud question";
        await viewModel.SendCommand.ExecuteAsync(null);
        Assert.Equal("cloud", assistant.LastSourceId);
        ModelMessage onlyMessage = Assert.Single(assistant.LastConversation!);
        Assert.Equal("cloud question", onlyMessage.Content);

        viewModel.SelectedModelSource = viewModel.ModelSources.Single(source => source.Id == "local");
        Assert.Equal(2, viewModel.Messages.Count);
        Assert.Equal("local-only context", viewModel.Messages[0].Content);
    }

    [Fact]
    public async Task ProjectContextAndPublicRunEventsProduceRealUsageTimeline()
    {
        var workspace = new InMemoryModProjectWorkspace();
        ModProjectSnapshot project = workspace.RegisterProject(Path.Combine(Path.GetTempPath(), "example-mod.jar"));
        var usage = ModelTokenUsage.FromProviderResponse(120, 30, 150)!;
        var assistant = new ProjectAwareAssistantService(usage);
        using var viewModel = new AssistantViewModel(
            assistant,
            new StubSelectionService(CreateSource("local")),
            projectWorkspace: workspace);
        viewModel.RefreshModelSources();
        viewModel.RefreshProjects();
        viewModel.Draft = "Inspect this mod and summarize the task.";

        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Equal(project.ProjectId, assistant.Project?.ProjectId);
        Assert.Equal(2, viewModel.Messages.Count);
        AssistantChatMessageViewModel response = viewModel.Messages[1];
        Assert.Equal("Project-aware answer.", response.Content);
        Assert.Equal(3, response.Activities.Count);
        Assert.Same(usage, response.ModelUsage);
        Assert.Contains("150", response.UsageSummary, StringComparison.Ordinal);
        Assert.False(response.IsRunning);
    }

    [Fact]
    public async Task EachModProjectRestoresItsOwnConversationBranch()
    {
        var workspace = new InMemoryModProjectWorkspace();
        ModProjectSnapshot first = workspace.RegisterProject(Path.Combine(Path.GetTempPath(), "first-mod.jar"));
        ModProjectSnapshot second = workspace.RegisterProject(
            Path.Combine(Path.GetTempPath(), "second-mod.jar"),
            makeActive: false);
        var assistant = new RecordingAssistantService(new ModelAssistantCompletion("answer", []));
        using var viewModel = new AssistantViewModel(
            assistant,
            new StubSelectionService(CreateSource("local")),
            projectWorkspace: workspace);
        viewModel.RefreshModelSources();
        viewModel.RefreshProjects();
        Assert.Equal(first.ProjectId, viewModel.SelectedProject?.ProjectId);
        viewModel.Draft = "first project question";
        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.SelectedProject = viewModel.Projects.Single(option => option.ProjectId == second.ProjectId);
        Assert.Empty(viewModel.Messages);
        viewModel.Draft = "second project question";
        await viewModel.SendCommand.ExecuteAsync(null);
        Assert.Equal("second project question", Assert.Single(assistant.LastConversation!).Content);

        viewModel.SelectedProject = viewModel.Projects.Single(option => option.ProjectId == first.ProjectId);
        Assert.Equal(2, viewModel.Messages.Count);
        Assert.Equal("first project question", viewModel.Messages[0].Content);
    }

    [Fact]
    public async Task ProjectProgressRefreshKeepsInFlightSessionAndContextSwitchClearsAuthorization()
    {
        var workspace = new InMemoryModProjectWorkspace();
        ModProjectSnapshot project = workspace.RegisterProject(Path.Combine(Path.GetTempPath(), "live-mod.jar"));
        ModProjectTaskSnapshot task = workspace.RegisterTask(
            project.ProjectId,
            new ModProjectTaskRegistration(
                project.SourceArtifactPath,
                Path.Combine(Path.GetTempPath(), "live-mod-output.jar"),
                "local",
                "zh_cn",
                TranslationStyle.Formal,
                "Translate live mod"));
        var jobId = Guid.NewGuid();
        workspace.AttachJob(task.TaskId, jobId, () => { });
        var assistant = new BlockingAssistantService();
        using var viewModel = new AssistantViewModel(
            assistant,
            new StubSelectionService(CreateSource("local"), CreateSource("cloud")),
            projectWorkspace: workspace);
        viewModel.RefreshModelSources();
        viewModel.RefreshProjects();
        viewModel.AllowProjectChanges = true;
        viewModel.Draft = "keep running";

        Task send = viewModel.SendCommand.ExecuteAsync(null);
        await assistant.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(viewModel.AllowProjectChanges);
        workspace.TryReportProgress(
            jobId,
            new TranslationQueueProgress(jobId, PipelineStage.Translating, 0.5),
            out _);
        await Task.Delay(25, TestContext.Current.CancellationToken);

        Assert.False(send.IsCompleted);
        Assert.Equal(project.ProjectId, viewModel.SelectedProject?.ProjectId);
        viewModel.AllowProjectChanges = true;
        viewModel.SelectedModelSource = viewModel.ModelSources.Single(source => source.Id == "cloud");
        Assert.False(viewModel.AllowProjectChanges);
        viewModel.CancelCommand.Execute(null);
        await send;
    }

    [Fact]
    public void RegisteringTaskForExistingActiveProjectFocusesItFromGeneralAssistant()
    {
        var workspace = new InMemoryModProjectWorkspace();
        ModProjectSnapshot project = workspace.RegisterProject(Path.Combine(Path.GetTempPath(), "focus-mod.jar"));
        using var viewModel = new AssistantViewModel(
            new RecordingAssistantService(new ModelAssistantCompletion("unused", [])),
            new StubSelectionService(CreateSource("local")),
            projectWorkspace: workspace);
        viewModel.RefreshModelSources();
        viewModel.RefreshProjects();
        viewModel.SelectedProject = viewModel.Projects.Single(option => option.ProjectId is null);
        Assert.False(viewModel.HasSelectedProject);

        _ = workspace.RegisterTask(
            project.ProjectId,
            new ModProjectTaskRegistration(
                project.SourceArtifactPath,
                Path.Combine(Path.GetTempPath(), "focus-mod-output.jar"),
                "local",
                "zh_cn",
                TranslationStyle.Formal,
                "Focus this task"));

        Assert.Equal(project.ProjectId, viewModel.SelectedProject?.ProjectId);
    }

    [Fact]
    public async Task FailedRequestRollsBackIncompleteTurnAndDoesNotExposeExceptionText()
    {
        using var viewModel = new AssistantViewModel(
            new ThrowingAssistantService(),
            new StubSelectionService(CreateSource("local")));
        viewModel.RefreshModelSources();
        viewModel.Draft = "retry me";

        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Messages);
        Assert.Equal("retry me", viewModel.Draft);
        Assert.DoesNotContain("credential=secret", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SafeProviderDiagnosticIsSurfacedWithoutLosingTheDraft()
    {
        using var viewModel = new AssistantViewModel(
            new DiagnosticThrowingAssistantService(),
            new StubSelectionService(CreateSource("local")));
        viewModel.RefreshModelSources();
        viewModel.Draft = "retry with the provider limit";

        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Messages);
        Assert.Equal("retry with the provider limit", viewModel.Draft);
        Assert.Contains("context window exceeded", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static ModelSource CreateSource(string id) => new(
        id,
        id,
        ModelProviderKind.Ollama,
        new Uri("http://127.0.0.1:11434"),
        "qwen3");

    private sealed class RecordingAssistantService(ModelAssistantCompletion completion) : IModelAssistantService
    {
        public string? LastSourceId { get; private set; }

        public IReadOnlyList<ModelMessage>? LastConversation { get; private set; }

        public Task<ModelAssistantCompletion> CompleteAsync(
            string modelSourceId,
            IReadOnlyList<ModelMessage> conversation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSourceId = modelSourceId;
            LastConversation = conversation;
            return Task.FromResult(completion);
        }
    }

    private sealed class BlockingAssistantService : IModelAssistantService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ModelAssistantCompletion> CompleteAsync(
            string modelSourceId,
            IReadOnlyList<ModelMessage> conversation,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ModelAssistantCompletion("unreachable", []);
        }
    }

    private sealed class ProjectAwareAssistantService(ModelTokenUsage usage) : IModelAssistantService
    {
        public ModProjectSnapshot? Project { get; private set; }

        public Task<ModelAssistantCompletion> CompleteAsync(
            string modelSourceId,
            IReadOnlyList<ModelMessage> conversation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The project-aware overload is required by this test.");

        public Task<ModelAssistantCompletion> CompleteAsync(
            string modelSourceId,
            IReadOnlyList<ModelMessage> conversation,
            ModProjectSnapshot? project,
            IProgress<ModelRunEvent>? progress,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Project = project;
            progress?.Report(new ModelRunEvent(1, ModelRunEventKind.ModelRoundStarted, 1));
            progress?.Report(new ModelRunEvent(2, ModelRunEventKind.ModelRoundCompleted, 1, Usage: usage));
            progress?.Report(new ModelRunEvent(3, ModelRunEventKind.RunCompleted, 1, Usage: usage));
            return Task.FromResult(new ModelAssistantCompletion(
                "Project-aware answer.",
                [],
                usage,
                "provider-model"));
        }
    }

    private sealed class SequenceAssistantService(params ModelAssistantCompletion[] completions) : IModelAssistantService
    {
        private int _index;

        public IReadOnlyList<ModelMessage>? LastConversation { get; private set; }

        public Task<ModelAssistantCompletion> CompleteAsync(
            string modelSourceId,
            IReadOnlyList<ModelMessage> conversation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastConversation = conversation;
            int current = _index++;
            if (current >= completions.Length)
            {
                throw new InvalidOperationException("No completion remains for this test.");
            }

            return Task.FromResult(completions[current]);
        }
    }

    private sealed class ThrowingAssistantService : IModelAssistantService
    {
        public Task<ModelAssistantCompletion> CompleteAsync(
            string modelSourceId,
            IReadOnlyList<ModelMessage> conversation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("credential=secret");
    }

    private sealed class DiagnosticThrowingAssistantService : IModelAssistantService
    {
        public Task<ModelAssistantCompletion> CompleteAsync(
            string modelSourceId,
            IReadOnlyList<ModelMessage> conversation,
            CancellationToken cancellationToken = default) =>
            throw new ModelServiceException("Provider context window exceeded.");
    }

    private sealed class StubSelectionService(params ModelSource[] sources) : IModelSelectionService
    {
        public IReadOnlyList<ModelSource> Sources { get; } = sources;

        public ModelSource? SelectedSource { get; private set; } = sources.FirstOrDefault();

        public Task<bool> SelectSourceAsync(
            string sourceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModelSource? source = Sources.FirstOrDefault(item => item.Id == sourceId);
            SelectedSource = source;
            return Task.FromResult(source is not null);
        }
    }
}
