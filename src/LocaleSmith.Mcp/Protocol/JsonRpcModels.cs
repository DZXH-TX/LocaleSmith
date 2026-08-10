using System.Text.Json;

namespace LocaleSmith.Mcp.Protocol;

internal readonly record struct JsonRpcId(string Key, JsonElement Value);

internal sealed record JsonRpcMessage(
    JsonRpcId? Id,
    string Method,
    JsonElement? Params,
    bool IsNotification);

internal sealed class JsonRpcProtocolException : Exception
{
    public JsonRpcProtocolException(int code, string message, JsonRpcId? id = null, object? data = null)
        : base(message)
    {
        Code = code;
        Id = id;
        DataValue = data;
    }

    public int Code { get; }

    public JsonRpcId? Id { get; }

    public object? DataValue { get; }
}

internal sealed class McpMessageTooLargeException : Exception
{
    public McpMessageTooLargeException(int maximumBytes)
        : base($"MCP message exceeds the configured {maximumBytes}-byte limit.")
    {
    }
}
