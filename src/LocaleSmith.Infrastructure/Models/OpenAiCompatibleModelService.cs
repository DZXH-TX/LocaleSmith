using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Infrastructure.Models;

public sealed class OpenAiCompatibleModelService : HttpModelServiceBase, IModelCatalogService
{
    private const int MaximumReasoningContentCharacters = 256 * 1024;
    private const int MaximumCatalogModels = 2048;
    private const int MaximumModelIdCharacters = 512;

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

        if (Source.UsesReasoningDetailsReplay)
        {
            body["reasoning_split"] = true;
        }

        if (Source.SupportsCustomTemperature && request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.MaxTokens is { } maxTokens &&
            Source.TokenLimitParameter != OpenAiTokenLimitParameter.Omit)
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

            ModelTokenUsage? tokenUsage = null;
            if (root.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                tokenUsage = CreateProviderUsage(
                    OptionalInt64(usage, "prompt_tokens"),
                    OptionalInt64(usage, "completion_tokens"),
                    OptionalInt64(usage, "total_tokens"),
                    "OpenAI-compatible response usage");
            }

            return new ModelResponse(
                content,
                OptionalString(root, "model"),
                toolCalls: toolCalls,
                reasoningContent: reasoningContent,
                usage: tokenUsage);
        }
    }

    public async Task<IReadOnlyList<AvailableModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        using var secret = await ResolveRequiredSecretAsync(cancellationToken).ConfigureAwait(false);
        string apiKey = secret.DangerousGetString();
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsEndpoint());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await HttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }

        catch (HttpRequestException exception)
        {
            throw CreateSafeNetworkException("OpenAI-compatible model catalog", exception, apiKey);
        }

        using (response)
        using (var document = await ReadSuccessfulJsonAsync(
                   response,
                   "OpenAI-compatible model catalog",
                   cancellationToken,
                   apiKey).ConfigureAwait(false))
        {
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                throw new ModelServiceException(
                    "OpenAI-compatible model catalog response did not contain a 'data' array.");
            }

            if (data.GetArrayLength() > MaximumCatalogModels)
            {
                throw new ModelServiceException(
                    $"OpenAI-compatible model catalog returned more than {MaximumCatalogModels} models.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var idValue) ||
                    idValue.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    continue;
                }

                string id = idValue.GetString()?.Trim() ?? string.Empty;
                if (id.Length is 0 or > MaximumModelIdCharacters ||
                    id.Any(static character => char.IsControl(character)))
                {
                    continue;
                }

                names.Add(id);
            }

            return names
                .Order(StringComparer.Ordinal)
                .Select(static name => new AvailableModelInfo(
                    name,
                    Digest: null,
                    SizeBytes: null,
                    ModifiedAt: null,
                    Family: null,
                    ParameterSize: null,
                    QuantizationLevel: null))
                .ToArray();
        }
    }

    private Uri BuildModelsEndpoint()
    {
        var builder = new UriBuilder(Source.Endpoint);
        string path = builder.Path.TrimEnd('/');
        const string chatCompletionsSuffix = "/chat/completions";
        if (path.EndsWith(chatCompletionsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^chatCompletionsSuffix.Length].TrimEnd('/');
        }

        if (!path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            path = string.IsNullOrEmpty(path) ? "/models" : $"{path}/models";
        }

        builder.Path = path;
        return builder.Uri;
    }

    private object ToOpenAiMessage(ModelMessage message)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = RoleName(message.Role),
            ["content"] = message.Role == ModelMessageRole.Assistant && message.Content.Length == 0
                ? Source.RequiresNonNullToolCallContent && message.ToolCalls.Count > 0
                    ? string.Empty
                    : null
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

            if (Source.UsesReasoningDetailsReplay)
            {
                JsonNode? reasoningDetails;
                try
                {
                    reasoningDetails = JsonNode.Parse(reasoningContent);
                }
                catch (System.Text.Json.JsonException exception)
                {
                    throw new ModelServiceException(
                        "Stored provider reasoning_details is not valid JSON.",
                        innerException: exception);
                }

                if (reasoningDetails is not JsonArray)
                {
                    throw new ModelServiceException(
                        "Stored provider reasoning_details must be a JSON array.");
                }

                result["reasoning_details"] = reasoningDetails;
            }
            else
            {
                result["reasoning_content"] = reasoningContent;
            }
        }

        return result;
    }

    private string? ParseReasoningContent(System.Text.Json.JsonElement message)
    {
        if (!Source.RequiresReasoningContentReplay)
        {
            return null;
        }

        string propertyName = Source.UsesReasoningDetailsReplay
            ? "reasoning_details"
            : "reasoning_content";
        if (!message.TryGetProperty(propertyName, out var reasoningContent) ||
            reasoningContent.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return null;
        }

        string value;
        if (Source.UsesReasoningDetailsReplay)
        {
            if (reasoningContent.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                throw new ModelServiceException(
                    "OpenAI-compatible response 'choices[0].message.reasoning_details' must be an array or null.");
            }

            value = reasoningContent.GetRawText();
        }
        else if (reasoningContent.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            value = reasoningContent.GetString()!;
        }
        else
        {
            throw new ModelServiceException(
                "OpenAI-compatible response 'choices[0].message.reasoning_content' must be a string or null.");
        }

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

    private static ModelTokenUsage? CreateProviderUsage(
        long? inputTokens,
        long? outputTokens,
        long? totalTokens,
        string description)
    {
        try
        {
            return ModelTokenUsage.FromProviderResponse(inputTokens, outputTokens, totalTokens);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new ModelServiceException($"{description} contained invalid token counts.", innerException: exception);
        }
    }
}
