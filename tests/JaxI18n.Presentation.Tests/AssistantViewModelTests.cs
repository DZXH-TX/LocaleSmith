using JaxI18n.Core.Models;
using JaxI18n.Presentation.Abstractions;
using JaxI18n.Presentation.Models;
using JaxI18n.Presentation.ViewModels;

namespace JaxI18n.Presentation.Tests;

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
    public async Task SwitchingModelSourceClearsConversationBeforeAnotherProviderReceivesIt()
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
        Assert.Contains("cross-provider", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        viewModel.Draft = "cloud question";
        await viewModel.SendCommand.ExecuteAsync(null);
        Assert.Equal("cloud", assistant.LastSourceId);
        ModelMessage onlyMessage = Assert.Single(assistant.LastConversation!);
        Assert.Equal("cloud question", onlyMessage.Content);
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
