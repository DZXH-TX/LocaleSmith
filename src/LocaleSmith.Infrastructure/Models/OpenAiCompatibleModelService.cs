using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Infrastructure.Models;

public sealed class OpenAiCompatibleModelService : HttpModelServiceBase
{
    private const int MaximumReasoningContentCharacters = 256 * 1024;

    public OpenAiCompatibleModelService(HttpClient httpClient, ModelSource source, ISecretResolver secretResolver)
        : base(httpClient, source, secretResolver)
    {
        if (source.Provider != ModelProviderKind.OpenAiCompatible)
        {
            throw new ArgumentException("The source provider must be OpenAI-compatible.", nameof(source));
        }
    }

    public override async Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = Source.ModelName,
            ["messages"] = request.Messages.Select(ToOpenAiMessage).ToArray()
        };
        if (request.Tools.Count > 0)
        {
            body["tools"] = request.Tools.Select(static tool => new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.InputSchema
                }
            });
        }

        if (Source.SupportsCustomTemperature && request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.MaxTokens is { } maxTokens)
        {
            body[Source.TokenLimitParameter == OpenAiTokenLimitParameter.MaxCompletionTokens
                ? "max_completion_tokens"
                : "max_tokens"] = maxTokens;
        }

        using var secret = await ResolveRequiredSecretAsync(cancellationToken).ConfigureAwait(false);
        var apiKey = secret.DangerousGetString();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint("chat/completions"))
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await HttpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw CreateSafeNetworkException("OpenAI-compatible endpoint", exception, apiKey);
        }

        using (response)
        {
            using var document = await ReadSuccessfulJsonAsync(
                    response,
                    "OpenAI-compatible endpoint",
                    cancellationToken,
                    apiKey)
                .ConfigureAwait(false);
            var root = document.RootElement;

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != System.Text.Json.JsonValueKind.Array ||
                choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out var message) ||
                message.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw new ModelServiceException("OpenAI-compatible response did not contain 'choices[0].message'.");
            }

            var content = OptionalString(message, "content") ?? string.Empty;
            var reasoningContent = ParseReasoningContent(message);
            var toolCalls = ParseToolCalls(message);
            if (content.Length == 0 && toolCalls.Count == 0)
            {
                throw new ModelServiceException(
                    "OpenAI-compatible response contained neither message content nor tool calls.");
            }

            int? inputTokens = null;
            int? outputTokens = null;
            if (root.TryGetProperty("usage", out var usage))
            {
                inputTokens = OptionalInt32(usage, "prompt_tokens");
                outputTokens = OptionalInt32(usage, "completion_tokens");
            }

            return new ModelResponse(
                content,
                OptionalString(root, "model"),
                inputTokens,
                outputTokens,
                toolCalls,
                reasoningContent);
        }
    }

    private object ToOpenAiMessage(ModelMessage message)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = RoleName(message.Role),
            ["content"] = message.Role == ModelMessageRole.Assistant && message.Content.Length == 0
                ? null
                : message.Content
        };
        if (message.Role == ModelMessageRole.Assistant && message.ToolCalls.Count > 0)
        {
            result["tool_calls"] = message.ToolCalls.Select(static call => new
            {
                id = call.Id,
                type = "function",
                function = new
                {
                    name = call.Name,
                    arguments = call.Arguments.GetRawText()
                }
            });
        }
        else if (message.Role == ModelMessageRole.Tool)
        {
            result["tool_call_id"] = message.ToolCallId;
        }

        if (Source.RequiresReasoningContentReplay &&
            message.Role == ModelMessageRole.Assistant &&
            message.ReasoningContent is { } reasoningContent)
        {
            if (reasoningContent.Length > MaximumReasoningContentCharacters)
            {
                throw new ModelServiceException(
                    $"OpenAI-compatible request reasoning content exceeds {MaximumReasoningContentCharacters} characters.");
            }

            result["reasoning_content"] = reasoningContent;
        }

        return result;
    }

    private string? ParseReasoningContent(System.Text.Json.JsonElement message)
    {
        if (!Source.RequiresReasoningContentReplay ||
            !message.TryGetProperty("reasoning_content", out var reasoningContent) ||
            reasoningContent.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return null;
        }

        if (reasoningContent.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            throw new ModelServiceException(
                "OpenAI-compatible response 'choices[0].message.reasoning_content' must be a string or null.");
        }

        var value = reasoningContent.GetString()!;
        if (value.Length > MaximumReasoningContentCharacters)
        {
            throw new ModelServiceException(
                $"OpenAI-compatible response reasoning content exceeds {MaximumReasoningContentCharacters} characters.");
        }

        return value;
    }

    private static ReadOnlyCollection<ModelToolCall> ParseToolCalls(System.Text.Json.JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var calls) ||
            calls.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return Array.AsReadOnly(Array.Empty<ModelToolCall>());
        }

        if (calls.ValueKind != System.Text.Json.JsonValueKind.Array || calls.GetArrayLength() > 32)
        {
            throw new ModelServiceException(
                "OpenAI-compatible response 'choices[0].message.tool_calls' must be an array of at most 32 calls.");
        }

        var result = new List<ModelToolCall>(calls.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in calls.EnumerateArray())
        {
            if (call.ValueKind != System.Text.Json.JsonValueKind.Object ||
                OptionalString(call, "id") is not { } id ||
                !ids.Add(id) ||
                !call.TryGetProperty("function", out var function) ||
                function.ValueKind != System.Text.Json.JsonValueKind.Object ||
                OptionalString(function, "name") is not { } name ||
                OptionalString(function, "arguments") is not { } arguments)
            {
                throw new ModelServiceException(
                    "OpenAI-compatible response contained an invalid or duplicate tool call.");
            }

            try
            {
                result.Add(new ModelToolCall(
                    id,
                    name,
                    ParseToolArguments(arguments, "choices[0].message.tool_calls[].function.arguments")));
            }
            catch (ArgumentException exception)
            {
                throw new ModelServiceException(
                    "OpenAI-compatible response contained invalid tool-call metadata.",
                    innerException: exception);
            }
        }

        return result.AsReadOnly();
    }
}
