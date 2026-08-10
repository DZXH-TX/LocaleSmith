using System.Text.Json;

namespace LocaleSmith.Mcp.Protocol;

internal sealed class JsonRpcWriter : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly Stream _output;
    private readonly int _maximumMessageBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonRpcWriter(Stream output, int maximumMessageBytes)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _maximumMessageBytes = maximumMessageBytes;
    }

    public Task WriteResultAsync(JsonRpcId id, object result, CancellationToken cancellationToken)
    {
        var message = new { jsonrpc = "2.0", id = id.Value, result };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        if (bytes.Length > _maximumMessageBytes)
        {
            return WriteErrorAsync(
                id,
                -32001,
                "Response exceeds the configured message-size limit.",
                null,
                cancellationToken);
        }

        return WriteBytesAsync(bytes, cancellationToken);
    }

    public Task WriteErrorAsync(
        JsonRpcId? id,
        int code,
        string message,
        object? data,
        CancellationToken cancellationToken) =>
        WriteAsync(
            new
            {
                jsonrpc = "2.0",
                id = id?.Value,
                error = new { code, message, data }
            },
            cancellationToken);

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        if (bytes.Length > _maximumMessageBytes)
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    jsonrpc = "2.0",
                    id = (object?)null,
                    error = new { code = -32001, message = "Response exceeds the configured message-size limit." }
                },
                SerializerOptions);
        }

        await WriteBytesAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
