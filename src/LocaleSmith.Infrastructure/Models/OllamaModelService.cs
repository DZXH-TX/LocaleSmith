using System.Collections.ObjectModel;
using System.Net.Http.Json;
using System.Text.Json;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Infrastructure.Models;

public sealed class OllamaModelService : HttpModelServiceBase, IModelCatalogService
{
    private const int MaximumCatalogEntries = 10_000;

    public OllamaModelService(HttpClient httpClient, ModelSource source, ISecretResolver secretResolver)
        : base(httpClient, source, secretResolver)
    {
        if (source.Provider != ModelProviderKind.Ollama)
        {
            throw new ArgumentException("The source provider must be Ollama.", nameof(source));
        }
    }

    public override async Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new
        {
            model = Source.ModelName,
            stream = false,
            messages = request.Messages.Select(ToOllamaMessage),
            tools = request.Tools.Count == 0
                ? null
                : request.Tools.Select(static tool => new
                {
                    type = "function",
                    function = new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        parameters = tool.InputSchema
                    }
                }),
            options = request.Temperature is null && request.MaxTokens is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["temperature"] = request.Temperature,
                    ["num_predict"] = request.MaxTokens
                }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint("api/chat"))
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        using var response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        using var document = await ReadSuccessfulJsonAsync(response, "Ollama", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            throw new ModelServiceException("Ollama response did not contain 'message'.");
        }

        var content = OptionalString(message, "content") ?? string.Empty;
        var toolCalls = ParseToolCalls(message);
        if (content.Length == 0 && toolCalls.Count == 0)
        {
            throw new ModelServiceException("Ollama response contained neither message content nor tool calls.");
        }

        return new ModelResponse(
            content,
            OptionalString(root, "model"),
            OptionalInt32(root, "prompt_eval_count"),
            OptionalInt32(root, "eval_count"),
            toolCalls);
    }

    public async Task<IReadOnlyList<AvailableModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildEndpoint("api/tags"));
        using var response = await HttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        using var document = await ReadSuccessfulJsonAsync(response, "Ollama", cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
        {
            throw new ModelServiceException("Ollama model catalog did not contain a 'models' array.");
        }

        if (models.GetArrayLength() > MaximumCatalogEntries)
        {
            throw new ModelServiceException(
                $"Ollama returned more than {MaximumCatalogEntries} model catalog entries.");
        }

        var result = new List<AvailableModelInfo>(models.GetArrayLength());
        foreach (var model in models.EnumerateArray())
        {
            var name = OptionalString(model, "name") ?? OptionalString(model, "model");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            DateTimeOffset? modifiedAt = null;
            if (model.TryGetProperty("modified_at", out var modifiedValue) &&
                modifiedValue.ValueKind == JsonValueKind.String &&
                modifiedValue.TryGetDateTimeOffset(out var parsedModifiedAt))
            {
                modifiedAt = parsedModifiedAt;
            }

            long? size = null;
            if (model.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var parsedSize))
            {
                size = parsedSize;
            }

            var details = model.TryGetProperty("details", out var detailValue) &&
                detailValue.ValueKind == JsonValueKind.Object
                    ? detailValue
                    : default;
            result.Add(new AvailableModelInfo(
                name,
                OptionalString(model, "digest"),
                size,
                modifiedAt,
                details.ValueKind == JsonValueKind.Object ? OptionalString(details, "family") : null,
                details.ValueKind == JsonValueKind.Object ? OptionalString(details, "parameter_size") : null,
                details.ValueKind == JsonValueKind.Object ? OptionalString(details, "quantization_level") : null));
        }

        return result
            .OrderBy(static model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static object ToOllamaMessage(ModelMessage message)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = RoleName(message.Role),
            ["content"] = message.Content
        };
        if (message.Role == ModelMessageRole.Assistant && message.ToolCalls.Count > 0)
        {
            result["tool_calls"] = message.ToolCalls.Select(static call => new
            {
                type = "function",
                function = new
                {
                    name = call.Name,
                    arguments = call.Arguments
                }
            });
        }
        else if (message.Role == ModelMessageRole.Tool)
        {
            result["tool_name"] = message.ToolName;
        }

        return result;
    }

    private static ReadOnlyCollection<ModelToolCall> ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind == JsonValueKind.Null)
        {
            return Array.AsReadOnly(Array.Empty<ModelToolCall>());
        }

        if (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() > 32)
        {
            throw new ModelServiceException("Ollama response 'message.tool_calls' must be an array of at most 32 calls.");
        }

        var result = new List<ModelToolCall>(calls.GetArrayLength());
        var index = 0;
        foreach (var call in calls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object ||
                !call.TryGetProperty("function", out var function) ||
                function.ValueKind != JsonValueKind.Object ||
                OptionalString(function, "name") is not { } name ||
                !function.TryGetProperty("arguments", out var arguments))
            {
                throw new ModelServiceException("Ollama response contained an invalid tool call.");
            }

            try
            {
                result.Add(new ModelToolCall(
                    $"ollama-{Guid.NewGuid():N}-{index:D2}",
                    name,
                    RequireObject(arguments, "message.tool_calls[].function.arguments")));
            }
            catch (ArgumentException exception)
            {
                throw new ModelServiceException(
                    "Ollama response contained invalid tool-call metadata.",
                    innerException: exception);
            }

            index++;
        }

        return result.AsReadOnly();
    }
}
