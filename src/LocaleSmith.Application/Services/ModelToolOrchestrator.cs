using LocaleSmith.Application.Models;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Application.Services;

public sealed class ModelToolOrchestrator
{
    private const int DefaultMaximumRounds = 8;
    private const int MaximumTotalCalls = 32;
    private const int MaximumToolResultCharacters = 64 * 1024;
    private const int MaximumAssistantContentCharacters = 256 * 1024;
    private const int MaximumReasoningContentCharacters = 256 * 1024;
    private const int MaximumTranscriptCharacters = 1024 * 1024;
    private readonly int _maximumRounds;

    public ModelToolOrchestrator(int maximumRounds = DefaultMaximumRounds)
    {
        if (maximumRounds is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRounds), "Maximum rounds must be between 1 and 16.");
        }

        _maximumRounds = maximumRounds;
    }

    public Task<ModelResponse> CompleteAsync(
        IModelService service,
        ModelRequest request,
        IModelToolExecutor toolExecutor,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(service, request, toolExecutor, progress: null, cancellationToken);

    public async Task<ModelResponse> CompleteAsync(
        IModelService service,
        ModelRequest request,
        IModelToolExecutor toolExecutor,
        IProgress<ModelRunEvent>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolExecutor);

        var availableTools = toolExecutor.Tools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
        if (availableTools.Count == 0)
        {
            throw new ArgumentException("At least one executable model tool is required.", nameof(toolExecutor));
        }

        IReadOnlyList<ModelToolDefinition> exposedTools;
        if (request.Tools.Count == 0)
        {
            exposedTools = availableTools.Values.OrderBy(static tool => tool.Name, StringComparer.Ordinal).ToArray();
        }
        else
        {
            foreach (ModelToolDefinition tool in request.Tools)
            {
                if (!availableTools.ContainsKey(tool.Name))
                {
                    throw new ArgumentException(
                        $"Request tool '{tool.Name}' has no matching executor.",
                        nameof(request));
                }
            }

            exposedTools = request.Tools
                .Select(tool => availableTools[tool.Name])
                .ToArray();
        }

        var messages = request.Messages.ToList();
        if (messages.Any(static message =>
                message.ReasoningContent is { Length: > MaximumReasoningContentCharacters }))
        {
            throw new ModelServiceException(
                $"The initial tool transcript contains reasoning content longer than " +
                $"{MaximumReasoningContentCharacters} characters.");
        }

        var transcriptCharacters = messages.Sum(EstimateMessageCharacters);
        if (transcriptCharacters > MaximumTranscriptCharacters)
        {
            throw new ModelServiceException(
                $"The initial tool transcript exceeds {MaximumTranscriptCharacters} characters.");
        }

        var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
        var totalCalls = 0;
        ModelTokenUsage usage = ModelTokenUsage.Empty;
        var eventSequence = 0;
        var currentRound = 0;
        var providerCallInFlight = false;

        try
        {
            for (var round = 0; round < _maximumRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentRound = round + 1;
                Report(
                    progress,
                    ref eventSequence,
                    ModelRunEventKind.ModelRoundStarted,
                    currentRound);
                var roundRequest = new ModelRequest(
                    messages,
                    request.Temperature,
                    request.MaxTokens,
                    exposedTools);
                providerCallInFlight = true;
                ModelResponse response = await service
                    .CompleteAsync(roundRequest, cancellationToken)
                    .ConfigureAwait(false);
                providerCallInFlight = false;
                usage = usage.AddProviderCall(response.Usage);
                Report(
                    progress,
                    ref eventSequence,
                    ModelRunEventKind.ModelRoundCompleted,
                    currentRound,
                    usage: response.Usage ?? ModelTokenUsage.MissingProviderCall);
                if (response.Content.Length > MaximumAssistantContentCharacters)
                {
                    throw new ModelServiceException(
                        $"The model response exceeds {MaximumAssistantContentCharacters} characters.");
                }

                if (response.ReasoningContent is { Length: > MaximumReasoningContentCharacters })
                {
                    throw new ModelServiceException(
                        $"The model reasoning content exceeds {MaximumReasoningContentCharacters} characters.");
                }

                if (response.ToolCalls.Count == 0)
                {
                    Report(
                        progress,
                        ref eventSequence,
                        ModelRunEventKind.RunCompleted,
                        currentRound,
                        usage: usage);
                    return new ModelResponse(
                        response.Content,
                        response.Model,
                        reasoningContent: response.ReasoningContent,
                        usage: usage);
                }

                totalCalls = checked(totalCalls + response.ToolCalls.Count);
                if (totalCalls > MaximumTotalCalls)
                {
                    throw new ModelServiceException(
                        $"The model requested more than {MaximumTotalCalls} tool calls in one conversation.");
                }

                foreach (ModelToolCall call in response.ToolCalls)
                {
                    if (!seenCallIds.Add(call.Id))
                    {
                        throw new ModelServiceException($"The model reused tool-call id '{call.Id}'.");
                    }
                }

                AddMessageWithinBudget(
                    messages,
                    new ModelMessage(
                        ModelMessageRole.Assistant,
                        response.Content,
                        response.ToolCalls,
                        reasoningContent: response.ReasoningContent),
                    ref transcriptCharacters);
                foreach (ModelToolCall call in response.ToolCalls)
                {
                    Report(
                        progress,
                        ref eventSequence,
                        ModelRunEventKind.ToolStarted,
                        currentRound,
                        call.Name);
                    ModelToolResult result;
                    if (!availableTools.ContainsKey(call.Name) || !exposedTools.Any(tool => tool.Name == call.Name))
                    {
                        result = new ModelToolResult(
                            call.Id,
                            call.Name,
                            "The requested tool is not available in this conversation.",
                            IsError: true);
                    }
                    else
                    {
                        try
                        {
                            result = (await toolExecutor
                                    .ExecuteAsync(call, cancellationToken)
                                    .ConfigureAwait(false))
                                .Normalize();
                            if (!string.Equals(result.ToolCallId, call.Id, StringComparison.Ordinal) ||
                                !string.Equals(result.ToolName, call.Name, StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException("The tool executor returned a mismatched correlation id or name.");
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception) when (
                            exception is not OutOfMemoryException and
                            not AccessViolationException)
                        {
                            result = new ModelToolResult(
                                call.Id,
                                call.Name,
                                $"Tool execution failed: {exception.GetType().Name}.",
                                IsError: true);
                        }
                    }

                    Report(
                        progress,
                        ref eventSequence,
                        result.IsError ? ModelRunEventKind.ToolFailed : ModelRunEventKind.ToolCompleted,
                        currentRound,
                        call.Name,
                        taskId: result.PublicTaskId);
                    string boundedContent = result.Content.Length <= MaximumToolResultCharacters
                        ? result.Content
                        : string.Concat(result.Content.AsSpan(0, MaximumToolResultCharacters), "\n[tool result truncated]");
                    AddMessageWithinBudget(
                        messages,
                        new ModelMessage(
                            ModelMessageRole.Tool,
                            boundedContent,
                            toolCallId: call.Id,
                            toolName: call.Name,
                            toolResultIsError: result.IsError),
                        ref transcriptCharacters);
                }
            }

            throw new ModelServiceException(
                $"The model did not finish after the configured {_maximumRounds} tool-call rounds.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (providerCallInFlight)
            {
                usage = usage.AddProviderCall(null);
            }

            Report(
                progress,
                ref eventSequence,
                ModelRunEventKind.RunCancelled,
                currentRound,
                usage: usage);
            throw;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not AccessViolationException)
        {
            if (providerCallInFlight)
            {
                usage = usage.AddProviderCall(null);
            }

            Report(
                progress,
                ref eventSequence,
                ModelRunEventKind.RunFailed,
                currentRound,
                usage: usage);
            throw;
        }
    }

    private static void Report(
        IProgress<ModelRunEvent>? progress,
        ref int sequence,
        ModelRunEventKind kind,
        int round,
        string? toolName = null,
        ModelTokenUsage? usage = null,
        Guid? taskId = null)
    {
        if (progress is null)
        {
            return;
        }

        var modelEvent = new ModelRunEvent(
            checked(++sequence),
            kind,
            round,
            toolName,
            usage,
            taskId);
        try
        {
            progress.Report(modelEvent);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not AccessViolationException)
        {
            // Observability is best effort and must never change the model or tool result.
        }
    }

    private static void AddMessageWithinBudget(
        List<ModelMessage> messages,
        ModelMessage message,
        ref int transcriptCharacters)
    {
        int updated = checked(transcriptCharacters + EstimateMessageCharacters(message));
        if (updated > MaximumTranscriptCharacters)
        {
            throw new ModelServiceException(
                $"The accumulated tool transcript exceeds {MaximumTranscriptCharacters} characters.");
        }

        messages.Add(message);
        transcriptCharacters = updated;
    }

    private static int EstimateMessageCharacters(ModelMessage message)
    {
        int total = checked(message.Content.Length + (message.ReasoningContent?.Length ?? 0));
        foreach (ModelToolCall call in message.ToolCalls)
        {
            total = checked(total + call.Id.Length + call.Name.Length + call.Arguments.GetRawText().Length);
        }

        if (message.ToolCallId is { } toolCallId)
        {
            total = checked(total + toolCallId.Length);
        }

        if (message.ToolName is { } toolName)
        {
            total = checked(total + toolName.Length);
        }

        return total;
    }
}
