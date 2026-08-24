using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Infrastructure.Models;

public abstract class HttpModelServiceBase : IModelService
{
    private const long MaximumResponseBodyBytes = 16L * 1024 * 1024;
    private const int MaximumErrorCharacters = 4096;
    private const int MaximumErrorSummaryCharacters = 512;
    private const int MaximumToolArgumentsCharacters = 64 * 1024;
    private static readonly Regex BearerCredentialPattern = new(
        @"\bBearer\s+[^\s,;\""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex CommonApiKeyPattern = new(
        @"\b(?:sk|api)[-_][A-Za-z0-9][A-Za-z0-9._-]{7,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex CredentialAssignmentPattern = new(
        @"(?<label>\b(?:api[_-]?key|authorization|access[_-]?token|token|secret|credential)\b[\""']?\s*[:=]\s*[\""']?)(?:Bearer\s+)?[^\s,;\""'}]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ISecretResolver _secretResolver;

    protected HttpModelServiceBase(HttpClient httpClient, ModelSource source, ISecretResolver secretResolver)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _secretResolver = secretResolver ?? throw new ArgumentNullException(nameof(secretResolver));
    }

    protected HttpClient HttpClient { get; }

    public ModelSource Source { get; }

    public abstract Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);

    protected Uri BuildEndpoint(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalizedRelativePath = relativePath.Trim('/');
        var endpointPath = Source.Endpoint.AbsolutePath.TrimEnd('/');
        if (endpointPath.EndsWith('/' + normalizedRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return new UriBuilder(Source.Endpoint) { Path = endpointPath }.Uri;
        }

        var baseUri = Source.Endpoint.AbsoluteUri.EndsWith('/')
            ? Source.Endpoint
            : new Uri(Source.Endpoint.AbsoluteUri + '/', UriKind.Absolute);
        return new Uri(baseUri, normalizedRelativePath);
    }

    protected async ValueTask<SecretValue> ResolveRequiredSecretAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Source.ApiKeyReference))
        {
            throw new InvalidOperationException($"Model source '{Source.Id}' does not have an API key reference.");
        }

        var secret = await _secretResolver.ResolveAsync(Source.ApiKeyReference, cancellationToken).ConfigureAwait(false);
        return secret ?? throw new InvalidOperationException(
            $"No credential is stored for model source '{Source.Id}'.");
    }

    protected static JsonContent CreateJsonContent<T>(T value) => JsonContent.Create(value, options: JsonOptions);

    protected static ModelServiceException CreateSafeNetworkException(
        string providerName,
        HttpRequestException exception,
        string? credentialToRedact)
    {
        var summary = CreateSafeErrorSummary(exception.Message, credentialToRedact);
        return new ModelServiceException(string.IsNullOrWhiteSpace(summary)
            ? $"{providerName} network request failed."
            : $"{providerName} network request failed: {summary}");
    }

    protected async Task<JsonDocument> ReadSuccessfulJsonAsync(
        HttpResponseMessage response,
        string providerName,
        CancellationToken cancellationToken,
        string? credentialToRedact = null)
    {
        EnsureSameOrigin(response, providerName);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadBoundedTextAsync(
                    response.Content,
                    MaximumErrorCharacters,
                    cancellationToken)
                .ConfigureAwait(false);
            var errorSummary = CreateSafeErrorSummary(errorBody, credentialToRedact);
            var requestId = GetRequestId(response, credentialToRedact);
            var diagnosticSuffix = string.IsNullOrWhiteSpace(errorSummary)
                ? string.Empty
                : $" Server message: {errorSummary}";
            var requestIdSuffix = requestId is null ? string.Empty : $" Request ID: {requestId}.";

            throw new ModelServiceException(
                $"{providerName} returned HTTP {(int)response.StatusCode} ({response.StatusCode})." +
                diagnosticSuffix + requestIdSuffix,
                response.StatusCode,
                errorSummary,
                requestId: requestId);
        }

        if (response.Content.Headers.ContentLength > MaximumResponseBodyBytes)
        {
            throw new ModelServiceException(
                $"{providerName} returned a response larger than the fixed 16 MiB safety limit. " +
                "This is not a Token quota; lower the response Token budget or translation batch size.");
        }

        try
        {
            await response.Content
                .LoadIntoBufferAsync(MaximumResponseBodyBytes, cancellationToken)
                .ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(
                    stream,
                    new JsonDocumentOptions { MaxDepth = 64 },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new ModelServiceException($"{providerName} returned malformed JSON.", innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ModelServiceException(
                $"{providerName} could not be buffered within the fixed 16 MiB response safety limit. " +
                "This is not a Token quota; lower the response Token budget or translation batch size.",
                innerException: exception);
        }
    }

    protected static string RoleName(ModelMessageRole role) => role switch
    {
        ModelMessageRole.System => "system",
        ModelMessageRole.User => "user",
        ModelMessageRole.Assistant => "assistant",
        ModelMessageRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown model message role.")
    };

    protected static string RequiredString(JsonElement element, string propertyPath)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new ModelServiceException($"Model response did not contain a string at '{propertyPath}'.");
        }

        return element.GetString()!;
    }

    protected static long? OptionalInt64(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    protected static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    protected static JsonElement ParseToolArguments(string json, string propertyPath)
    {
        if (json.Length > MaximumToolArgumentsCharacters)
        {
            throw new ModelServiceException($"Model response contained oversized tool arguments at '{propertyPath}'.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ModelServiceException(
                    $"Model response tool arguments at '{propertyPath}' must be a JSON object.");
            }

            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new ModelServiceException(
                $"Model response contained malformed tool arguments at '{propertyPath}'.",
                innerException: exception);
        }
    }

    protected static JsonElement RequireObject(JsonElement element, string propertyPath)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ModelServiceException($"Model response did not contain an object at '{propertyPath}'.");
        }

        if (element.GetRawText().Length > MaximumToolArgumentsCharacters)
        {
            throw new ModelServiceException($"Model response contained oversized tool arguments at '{propertyPath}'.");
        }

        return element.Clone();
    }

    private void EnsureSameOrigin(HttpResponseMessage response, string providerName)
    {
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null)
        {
            return;
        }

        if (!string.Equals(finalUri.Scheme, Source.Endpoint.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(finalUri.IdnHost, Source.Endpoint.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            finalUri.Port != Source.Endpoint.Port)
        {
            throw new ModelServiceException(
                $"{providerName} redirected the request to a different origin; credentials were not accepted for that response.");
        }
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        var buffer = new char[maximumCharacters];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await reader
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return new string(buffer, 0, total);
    }

    private static string? CreateSafeErrorSummary(string responseBody, string? credentialToRedact)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        var summary = TryReadProviderMessage(responseBody) ?? responseBody;
        if (!string.IsNullOrEmpty(credentialToRedact))
        {
            summary = summary.Replace(credentialToRedact, "[REDACTED]", StringComparison.Ordinal);
        }

        summary = BearerCredentialPattern.Replace(summary, "Bearer [REDACTED]");
        summary = CommonApiKeyPattern.Replace(summary, "[REDACTED]");
        summary = CredentialAssignmentPattern.Replace(summary, "${label}[REDACTED]");
        summary = NormalizeWhitespace(summary);
        if (summary.Length > MaximumErrorSummaryCharacters)
        {
            summary = summary[..MaximumErrorSummaryCharacters] + "…";
        }

        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }

    private static string? TryReadProviderMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "The provider returned a JSON error response without a message.";
            }

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }

                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var nestedMessage) &&
                    nestedMessage.ValueKind == JsonValueKind.String)
                {
                    return nestedMessage.GetString();
                }
            }

            foreach (var propertyName in new[] { "message", "detail" })
            {
                if (root.TryGetProperty(propertyName, out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }
            }

            return "The provider returned a JSON error response without a message.";
        }
        catch (JsonException)
        {
            // Plain-text and malformed error bodies are handled as bounded text below.
        }

        return null;
    }

    private static string NormalizeWhitespace(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaximumErrorSummaryCharacters + 1));
        var previousWasWhitespace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                if (!previousWasWhitespace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
            if (builder.Length > MaximumErrorSummaryCharacters)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }

    private static string? GetRequestId(HttpResponseMessage response, string? credentialToRedact)
    {
        foreach (var headerName in new[] { "x-request-id", "x-ds-trace-id", "request-id" })
        {
            if (!response.Headers.TryGetValues(headerName, out var values))
            {
                continue;
            }

            var requestId = NormalizeWhitespace(values.FirstOrDefault() ?? string.Empty);
            if (!string.IsNullOrEmpty(credentialToRedact))
            {
                requestId = requestId.Replace(credentialToRedact, "[REDACTED]", StringComparison.Ordinal);
            }

            requestId = BearerCredentialPattern.Replace(requestId, "Bearer [REDACTED]");
            requestId = CommonApiKeyPattern.Replace(requestId, "[REDACTED]");
            requestId = CredentialAssignmentPattern.Replace(requestId, "${label}[REDACTED]");
            if (requestId.Length is > 0 and <= 256 && requestId.All(static character => character is >= ' ' and <= '~'))
            {
                return requestId;
            }
        }

        return null;
    }
}
