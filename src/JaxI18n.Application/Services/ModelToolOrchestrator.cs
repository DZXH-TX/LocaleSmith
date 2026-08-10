using JaxI18n.Core.Abstractions;
using JaxI18n.Core.Models;

namespace JaxI18n.Application.Services;

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

    public async Task<ModelResponse> CompleteAsync(
        IModelService service,
        ModelRequest request,
        IModelToolExecutor toolExecutor,
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
        var inputTokens = 0;
        var outputTokens = 0;
        var inputKnown = false;
        var outputKnown = false;

        for (var round = 0; round < _maximumRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var roundRequest = new ModelRequest(
                messages,
                request.Temperature,
                request.MaxTokens,
                exposedTools);
            ModelResponse response = await service
                .CompleteAsync(roundRequest, cancellationToken)
                .ConfigureAwait(false);
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

            Accumulate(response.InputTokens, ref inputTokens, ref inputKnown);
            Accumulate(response.OutputTokens, ref outputTokens, ref outputKnown);

            if (response.ToolCalls.Count == 0)
            {
                return new ModelResponse(
                    response.Content,
                    response.Model,
                    inputKnown ? inputTokens : null,
                    outputKnown ? outputTokens : null,
                    reasoningContent: response.ReasoningContent);
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

    private static void Accumulate(int? value, ref int total, ref bool known)
    {
        if (value is null)
        {
            return;
        }

        total = checked(total + value.Value);
        known = true;
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
