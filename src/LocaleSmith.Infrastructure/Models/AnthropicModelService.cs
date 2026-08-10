using System.Collections.ObjectModel;
using System.Net.Http.Json;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Infrastructure.Models;

public sealed class AnthropicModelService : HttpModelServiceBase
{
    public AnthropicModelService(HttpClient httpClient, ModelSource source, ISecretResolver secretResolver)
        : base(httpClient, source, secretResolver)
    {
        if (source.Provider != ModelProviderKind.Anthropic)
        {
            throw new ArgumentException("The source provider must be Anthropic.", nameof(source));
        }
    }

    public override async Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var system = string.Join(
            "\n\n",
            request.Messages.Where(static message => message.Role == ModelMessageRole.System).Select(static message => message.Content));
        var messages = request.Messages
            .Where(static message => message.Role != ModelMessageRole.System)
            .Select(ToAnthropicMessage);
        var body = new
        {
            model = Source.ModelName,
            max_tokens = request.MaxTokens ?? 4096,
            system = string.IsNullOrEmpty(system) ? null : system,
            messages,
            tools = request.Tools.Count == 0
                ? null
                : request.Tools.Select(static tool => new
                {
                    name = tool.Name,
                    description = tool.Description,
                    input_schema = tool.InputSchema
                }),
            temperature = request.Temperature
        };

        using var secret = await ResolveRequiredSecretAsync(cancellationToken).ConfigureAwait(false);
        var apiKey = secret.DangerousGetString();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint("v1/messages"))
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        httpRequest.Headers.Add("x-api-key", apiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");

        HttpResponseMessage response;
        try
        {
            response = await HttpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw CreateSafeNetworkException("Anthropic", exception, apiKey);
        }

        using (response)
        {
            using var document = await ReadSuccessfulJsonAsync(response, "Anthropic", cancellationToken, apiKey)
                .ConfigureAwait(false);
            var root = document.RootElement;

            if (!root.TryGetProperty("content", out var content) ||
                content.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                throw new ModelServiceException("Anthropic response did not contain a content array.");
            }

            var text = string.Join(
                string.Empty,
                content.EnumerateArray()
                    .Where(static block =>
                        block.TryGetProperty("type", out var type) && type.GetString() == "text" && block.TryGetProperty("text", out _))
                    .Select(static block => RequiredString(block.GetProperty("text"), "content[].text")));
            var toolCalls = ParseToolCalls(content);
            if (text.Length == 0 && toolCalls.Count == 0)
            {
                throw new ModelServiceException("Anthropic response did not contain text or a tool_use content block.");
            }

            int? inputTokens = null;
            int? outputTokens = null;
            if (root.TryGetProperty("usage", out var usage))
            {
                inputTokens = OptionalInt32(usage, "input_tokens");
                outputTokens = OptionalInt32(usage, "output_tokens");
            }

            return new ModelResponse(
                text,
                OptionalString(root, "model"),
                inputTokens,
                outputTokens,
                toolCalls);
        }
    }

    private static object ToAnthropicMessage(ModelMessage message)
    {
        if (message.Role == ModelMessageRole.Tool)
        {
            return new
            {
                role = "user",
                content = new object[]
                {
                    new
                    {
                        type = "tool_result",
                        tool_use_id = message.ToolCallId,
                        content = message.Content,
                        is_error = message.ToolResultIsError
                    }
                }
            };
        }

        if (message.Role == ModelMessageRole.Assistant && message.ToolCalls.Count > 0)
        {
            var blocks = new List<object>(message.ToolCalls.Count + 1);
            if (message.Content.Length > 0)
            {
                blocks.Add(new { type = "text", text = message.Content });
            }

            blocks.AddRange(message.ToolCalls.Select(static call => (object)new
            {
                type = "tool_use",
                id = call.Id,
                name = call.Name,
                input = call.Arguments
            }));
            return new { role = "assistant", content = blocks };
        }

        return new
        {
            role = message.Role == ModelMessageRole.Assistant ? "assistant" : "user",
            content = message.Content
        };
    }

    private static ReadOnlyCollection<ModelToolCall> ParseToolCalls(System.Text.Json.JsonElement content)
    {
        var result = new List<ModelToolCall>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var type) || type.ValueKind != System.Text.Json.JsonValueKind.String ||
                !string.Equals(type.GetString(), "tool_use", StringComparison.Ordinal))
            {
                continue;
            }

            if (result.Count == 32 ||
                OptionalString(block, "id") is not { } id ||
                !ids.Add(id) ||
                OptionalString(block, "name") is not { } name ||
                !block.TryGetProperty("input", out var input))
            {
                throw new ModelServiceException("Anthropic response contained an invalid or duplicate tool_use block.");
            }

            try
            {
                result.Add(new ModelToolCall(id, name, RequireObject(input, "content[].input")));
            }
            catch (ArgumentException exception)
            {
                throw new ModelServiceException(
                    "Anthropic response contained invalid tool-use metadata.",
                    innerException: exception);
            }
        }

        return result.AsReadOnly();
    }
}
