using System.Globalization;
using System.Text.Json;

namespace JaxI18n.Mcp.Protocol;

internal static class JsonRpcParser
{
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "jsonrpc", "id", "method", "params"
    };

    public static JsonRpcMessage Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
        }
        catch (JsonException exception)
        {
            throw new JsonRpcProtocolException(-32700, "Parse error.", data: exception.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonRpcProtocolException(-32600, "Invalid Request: a JSON object is required.");
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!AllowedProperties.Contains(property.Name))
                {
                    throw new JsonRpcProtocolException(-32600, $"Invalid Request: unknown member '{property.Name}'.");
                }
            }

            if (!root.TryGetProperty("jsonrpc", out var version) ||
                version.ValueKind != JsonValueKind.String ||
                !string.Equals(version.GetString(), "2.0", StringComparison.Ordinal))
            {
                throw new JsonRpcProtocolException(-32600, "Invalid Request: jsonrpc must be '2.0'.");
            }

            if (!root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(methodElement.GetString()))
            {
                throw new JsonRpcProtocolException(-32600, "Invalid Request: method must be a non-empty string.");
            }

            var method = methodElement.GetString()!;
            if (method.Length > 128)
            {
                throw new JsonRpcProtocolException(-32600, "Invalid Request: method exceeds 128 characters.");
            }

            JsonRpcId? id = null;
            if (root.TryGetProperty("id", out var idElement))
            {
                id = ParseId(idElement);
            }

            JsonElement? parameters = null;
            if (root.TryGetProperty("params", out var paramsElement))
            {
                if (paramsElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                {
                    throw new JsonRpcProtocolException(-32602, "Invalid params: params must be an object or array.", id);
                }

                parameters = paramsElement.Clone();
            }

            return new JsonRpcMessage(id, method, parameters, id is null);
        }
    }

    private static JsonRpcId ParseId(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String when element.GetString() is { Length: <= 128 } value =>
                new JsonRpcId("s:" + value, element.Clone()),
            JsonValueKind.Number when element.TryGetInt64(out var value) =>
                new JsonRpcId("n:" + value.ToString(CultureInfo.InvariantCulture), element.Clone()),
            _ => throw new JsonRpcProtocolException(
                -32600,
                "Invalid Request: id must be a string of at most 128 characters or a 64-bit integer.")
        };
    }
}
