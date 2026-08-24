using System.Text.Json;
using LocaleSmith.Application.Models;
using LocaleSmith.Application.Services;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Application.Tests;

public sealed class ModelToolOrchestratorTests
{
    [Fact]
    public async Task ExecutesCorrelatedToolCallsAndReturnsFinalResponse()
    {
        ModelToolCall call = CreateCall("call-1", "system_context");
        var service = new SequenceModelService(
            new ModelResponse(string.Empty, inputTokens: 3, outputTokens: 1, toolCalls: [call]),
            new ModelResponse("final", inputTokens: 5, outputTokens: 2));
        var executor = new RecordingExecutor();
        var orchestrator = new ModelToolOrchestrator();

        ModelResponse response = await orchestrator.CompleteAsync(
            service,
            new ModelRequest([new ModelMessage(ModelMessageRole.User, "inspect")]),
            executor,
            TestContext.Current.CancellationToken);

        Assert.Equal("final", response.Content);
        Assert.Equal(8L, response.InputTokens);
        Assert.Equal(3L, response.OutputTokens);
        Assert.Equal(11L, response.TotalTokens);
        Assert.Equal(2, response.Usage!.ProviderCallCount);
        Assert.Equal(2, response.Usage.CallsWithUsage);
        Assert.Equal(2, response.Usage.CallsWithCompleteUsage);
        Assert.True(response.Usage.IsComplete);
        Assert.Equal("call-1", Assert.Single(executor.Calls).Id);
        Assert.Equal(2, service.Requests.Count);
        ModelRequest followUp = service.Requests[1];
        Assert.Equal(ModelMessageRole.Assistant, followUp.Messages[^2].Role);
        Assert.Equal(ModelMessageRole.Tool, followUp.Messages[^1].Role);
        Assert.Equal("safe result", followUp.Messages[^1].Content);
        Assert.Single(followUp.Tools);
    }

    [Fact]
    public async Task PreservesPrivateReasoningStateAcrossToolRound()
    {
        const string firstRoundReasoning = "\n  opaque \"trace\" 保留原样  \n";
        const string finalReasoning = "private final reasoning";
        var service = new SequenceModelService(
            new ModelResponse(
                string.Empty,
                toolCalls: [CreateCall("call-1", "system_context")],
                reasoningContent: firstRoundReasoning),
            new ModelResponse("visible final", reasoningContent: finalReasoning));

        ModelResponse response = await new ModelToolOrchestrator().CompleteAsync(
            service,
            new ModelRequest([new ModelMessage(ModelMessageRole.User, "inspect")]),
            new RecordingExecutor(),
            TestContext.Current.CancellationToken);

        ModelMessage replayedAssistant = service.Requests[1].Messages[^2];
        Assert.Equal(ModelMessageRole.Assistant, replayedAssistant.Role);
        Assert.Equal(firstRoundReasoning, replayedAssistant.ReasoningContent);
        Assert.Empty(replayedAssistant.Content);
        Assert.Equal("visible final", response.Content);
        Assert.Equal(finalReasoning, response.ReasoningContent);
        Assert.DoesNotContain(firstRoundReasoning, response.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishesContentFreeLifecycleEventsAndMarksMissingRoundUsageIncomplete()
    {
        const string privateReasoning = "private reasoning C:\\secret\\mod.jar";
        using var arguments = JsonDocument.Parse("""{"path":"C:\\secret\\mod.jar"}""");
        var call = new ModelToolCall("call-private", "system_context", arguments.RootElement);
        var service = new SequenceModelService(
            new ModelResponse(
                string.Empty,
                inputTokens: 3,
                outputTokens: 1,
                toolCalls: [call],
                reasoningContent: privateReasoning),
            new ModelResponse("visible final"));
        var progress = new RecordingProgress();
        var publicTaskId = Guid.NewGuid();

        ModelResponse response = await new ModelToolOrchestrator().CompleteAsync(
            service,
            new ModelRequest([new ModelMessage(ModelMessageRole.User, "inspect")]),
            new RecordingExecutor(publicTaskId: publicTaskId),
            progress,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                ModelRunEventKind.ModelRoundStarted,
                ModelRunEventKind.ModelRoundCompleted,
                ModelRunEventKind.ToolStarted,
                ModelRunEventKind.ToolCompleted,
                ModelRunEventKind.ModelRoundStarted,
                ModelRunEventKind.ModelRoundCompleted,
                ModelRunEventKind.RunCompleted
            ],
            progress.Events.Select(static item => item.Kind));
        Assert.Equal(Enumerable.Range(1, progress.Events.Count), progress.Events.Select(static item => item.Sequence));
        Assert.Equal("system_context", progress.Events.Single(item => item.Kind == ModelRunEventKind.ToolStarted).ToolName);
        Assert.Equal(
            publicTaskId,
            progress.Events.Single(item => item.Kind == ModelRunEventKind.ToolCompleted).TaskId);
        Assert.All(progress.Events, item =>
        {
            string display = item.ToString();
            Assert.DoesNotContain(privateReasoning, display, StringComparison.Ordinal);
            Assert.DoesNotContain(@"C:\secret\mod.jar", display, StringComparison.Ordinal);
            Assert.DoesNotContain("path", display, StringComparison.Ordinal);
        });
        Assert.Equal(2, response.Usage!.ProviderCallCount);
        Assert.Equal(1, response.Usage.CallsWithUsage);
        Assert.Equal(1, response.Usage.CallsWithCompleteUsage);
        Assert.Equal(3L, response.InputTokens);
        Assert.Equal(1L, response.OutputTokens);
        Assert.Equal(4L, response.TotalTokens);
        Assert.False(response.Usage.IsComplete);
        ModelRunEvent completed = progress.Events[^1];
        Assert.Same(response.Usage, completed.Usage);
    }

    [Fact]
    public async Task ProviderFailurePublishesSafeTerminalEventAndCountsMissingCall()
    {
        var progress = new RecordingProgress();
        var service = new ThrowingModelService();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ModelToolOrchestrator().CompleteAsync(
                service,
                new ModelRequest([new ModelMessage(ModelMessageRole.User, "inspect")]),
                new RecordingExecutor(),
                progress,
                TestContext.Current.CancellationToken));

        Assert.Contains("credential=secret", exception.Message, StringComparison.Ordinal);
        ModelRunEvent failed = progress.Events[^1];
        Assert.Equal(ModelRunEventKind.RunFailed, failed.Kind);
        Assert.Equal(1, failed.Usage!.ProviderCallCount);
        Assert.Equal(0, failed.Usage.CallsWithUsage);
        Assert.Equal(0, failed.Usage.CallsWithCompleteUsage);
        Assert.False(failed.Usage.IsComplete);
        Assert.DoesNotContain("credential=secret", failed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownModelToolIsReturnedAsErrorWithoutExecution()
    {
        var service = new SequenceModelService(
            new ModelResponse(string.Empty, toolCalls: [CreateCall("call-1", "unknown_tool")]),
            new ModelResponse("recovered"));
        var executor = new RecordingExecutor();

        ModelResponse response = await new ModelToolOrchestrator().CompleteAsync(
            service,
            new ModelRequest([new ModelMessage(ModelMessageRole.User, "inspect")]),
            executor,
            TestContext.Current.CancellationToken);

        Assert.Equal("recovered", response.Content);
        Assert.Empty(executor.Calls);
        ModelMessage error = service.Requests[1].Messages[^1];
        Assert.True(error.ToolResultIsError);
        Assert.Contains("not available", error.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateCallIdsAndEndlessLoopsFailClosed()
    {
        ModelToolCall call = CreateCall("same", "system_context");
        var duplicateService = new SequenceModelService(
            new ModelResponse(string.Empty, toolCalls: [call]),
            new ModelResponse(string.Empty, toolCalls: [call]));
        var executor = new RecordingExecutor();

        ModelServiceException duplicate = await Assert.ThrowsAsync<ModelServiceException>(() =>
            new ModelToolOrchestrator().CompleteAsync(
                duplicateService,
                new ModelRequest([new ModelMessage(ModelMessageRole.User, "inspect")]),
                executor,
                TestContext.Current.CancellationToken));
        Assert.Contains("reused", duplicate.Message, StringComparison.Ordinal);

        var endless = new GeneratedModelService(index =>
            new ModelResponse(string.Empty, toolCalls: [CreateCall($"call-{index}", "system_context")]));
        ModelServiceException maximum = await Assert.ThrowsAsync<ModelServiceException>(() =>
            new ModelToolOrchestrator(maximumRounds: 2).CompleteAsync(
                endless,
                new ModelRequest([new ModelMessage(ModelMessageRole.User, "inspect")]),
                executor,
                TestContext.Current.CancellationToken));
        Assert.Contains("did not finish", maximum.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutorFailureDoesNotExposeExceptionMessageToModel()
    {
        var service = new SequenceModelService(
            new ModelResponse(string.Empty, toolCalls: [CreateCall("call-1", "system_context")]),
            new ModelResponse("done"));
        var executor = new RecordingExecutor(throwOnExecute: true);
        var progress = new RecordingProgress();

        await new ModelToolOrchestrator().CompleteAsync(
            service,
            new ModelRequest([new ModelMessage(ModelMessageRole.User, "inspect")]),
            executor,
            progress,
            TestContext.Current.CancellationToken);

        ModelMessage result = service.Requests[1].Messages[^1];
        Assert.True(result.ToolResultIsError);
        Assert.Contains(nameof(InvalidOperationException), result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("credential=secret", result.Content, StringComparison.Ordinal);
        ModelRunEvent failed = Assert.Single(progress.Events, item => item.Kind == ModelRunEventKind.ToolFailed);
        Assert.Equal("system_context", failed.ToolName);
        Assert.DoesNotContain("credential=secret", failed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestCannotOverrideExecutorToolSchemaOrDescription()
    {
        using var untrustedSchema = JsonDocument.Parse(
            """{"type":"object","required":["danger"],"properties":{"danger":{"type":"string"}}}""");
        var service = new SequenceModelService(new ModelResponse("done"));
        var request = new ModelRequest(
            [new ModelMessage(ModelMessageRole.User, "inspect")],
            tools:
            [
                new ModelToolDefinition(
                    "system_context",
                    "Untrusted replacement description.",
                    untrustedSchema.RootElement)
            ]);

        await new ModelToolOrchestrator().CompleteAsync(
            service,
            request,
            new RecordingExecutor(),
            TestContext.Current.CancellationToken);

        ModelToolDefinition exposed = Assert.Single(Assert.Single(service.Requests).Tools);
        Assert.Equal("Read safe context.", exposed.Description);
        Assert.False(exposed.InputSchema.TryGetProperty("required", out _));
    }

    [Fact]
    public async Task OversizedResponseAndAccumulatedTranscriptFailClosed()
    {
        var executor = new RecordingExecutor();
        var oversized = new SequenceModelService(new ModelResponse(new string('x', 256 * 1024 + 1)));
        ModelServiceException responseLimit = await Assert.ThrowsAsync<ModelServiceException>(() =>
            new ModelToolOrchestrator().CompleteAsync(
                oversized,
                new ModelRequest([new ModelMessage(ModelMessageRole.User, "inspect")]),
                executor,
                TestContext.Current.CancellationToken));
        Assert.Contains("response exceeds", responseLimit.Message, StringComparison.Ordinal);

        var oversizedReasoning = new SequenceModelService(new ModelResponse(
            string.Empty,
            toolCalls: [CreateCall("reasoning-call", "system_context")],
            reasoningContent: new string('r', 256 * 1024 + 1)));
        ModelServiceException reasoningLimit = await Assert.ThrowsAsync<ModelServiceException>(() =>
            new ModelToolOrchestrator().CompleteAsync(
                oversizedReasoning,
                new ModelRequest([new ModelMessage(ModelMessageRole.User, "inspect")]),
                executor,
                TestContext.Current.CancellationToken));
        Assert.Contains("reasoning content exceeds", reasoningLimit.Message, StringComparison.Ordinal);

        var unusedService = new SequenceModelService(new ModelResponse("unused"));
        ModelServiceException initialReasoningLimit = await Assert.ThrowsAsync<ModelServiceException>(() =>
            new ModelToolOrchestrator().CompleteAsync(
                unusedService,
                new ModelRequest(
                [
                    new ModelMessage(
                        ModelMessageRole.Assistant,
                        "visible",
                        reasoningContent: new string('r', 256 * 1024 + 1))
                ]),
                executor,
                TestContext.Current.CancellationToken));
        Assert.Contains("initial tool transcript", initialReasoningLimit.Message, StringComparison.Ordinal);
        Assert.Empty(unusedService.Requests);

        var cumulative = new GeneratedModelService(index => new ModelResponse(
            new string('x', 200 * 1024),
            toolCalls: [CreateCall($"call-{index}", "system_context")]));
        ModelServiceException transcriptLimit = await Assert.ThrowsAsync<ModelServiceException>(() =>
            new ModelToolOrchestrator(maximumRounds: 8).CompleteAsync(
                cumulative,
                new ModelRequest([new ModelMessage(ModelMessageRole.User, "inspect")]),
                executor,
                TestContext.Current.CancellationToken));
        Assert.Contains("transcript exceeds", transcriptLimit.Message, StringComparison.Ordinal);
    }

    private static ModelToolCall CreateCall(string id, string name)
    {
        using var arguments = JsonDocument.Parse("{}");
        return new ModelToolCall(id, name, arguments.RootElement);
    }

    private sealed class RecordingExecutor : IModelToolExecutor
    {
        private readonly bool _throwOnExecute;
        private readonly Guid? _publicTaskId;

        public RecordingExecutor(bool throwOnExecute = false, Guid? publicTaskId = null)
        {
            _throwOnExecute = throwOnExecute;
            _publicTaskId = publicTaskId;
            using var schema = JsonDocument.Parse("""{"type":"object","additionalProperties":false}""");
            Tools = [new ModelToolDefinition("system_context", "Read safe context.", schema.RootElement)];
        }

        public IReadOnlyList<ModelToolDefinition> Tools { get; }

        public List<ModelToolCall> Calls { get; } = [];

        public Task<ModelToolResult> ExecuteAsync(
            ModelToolCall toolCall,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(toolCall);
            if (_throwOnExecute)
            {
                throw new InvalidOperationException("credential=secret");
            }

            return Task.FromResult(new ModelToolResult(
                toolCall.Id,
                toolCall.Name,
                "safe result",
                PublicTaskId: _publicTaskId));
        }
    }

    private sealed class SequenceModelService(params ModelResponse[] responses) : IModelService
    {
        private readonly Queue<ModelResponse> _responses = new(responses);

        public ModelSource Source { get; } = new(
            "test",
            "Test",
            ModelProviderKind.Ollama,
            new Uri("http://127.0.0.1:11434"),
            "test");

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

    private sealed class ThrowingModelService : IModelService
    {
        public ModelSource Source { get; } = new(
            "throwing",
            "Throwing",
            ModelProviderKind.Ollama,
            new Uri("http://127.0.0.1:11434"),
            "test");

        public Task<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("credential=secret");
    }

    private sealed class RecordingProgress : IProgress<ModelRunEvent>
    {
        public List<ModelRunEvent> Events { get; } = [];

        public void Report(ModelRunEvent value) => Events.Add(value);
    }

    private sealed class GeneratedModelService(Func<int, ModelResponse> factory) : IModelService
    {
        private int _index;

        public ModelSource Source { get; } = new(
            "test",
            "Test",
            ModelProviderKind.Ollama,
            new Uri("http://127.0.0.1:11434"),
            "test");

        public Task<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(factory(_index++));
        }
    }
}
