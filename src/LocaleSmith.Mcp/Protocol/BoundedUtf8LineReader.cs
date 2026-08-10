using System.Buffers;
using System.Text;

namespace LocaleSmith.Mcp.Protocol;

internal sealed class BoundedUtf8LineReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream _input;
    private readonly int _maximumBytes;
    private readonly byte[] _readBuffer = new byte[4096];
    private int _bufferOffset;
    private int _bufferLength;

    public BoundedUtf8LineReader(Stream input, int maximumBytes)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _maximumBytes = maximumBytes;
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new ArrayBufferWriter<byte>(Math.Min(_maximumBytes, 4096));
        var tooLarge = false;

        while (true)
        {
            if (_bufferOffset == _bufferLength)
            {
                _bufferLength = await _input.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
                _bufferOffset = 0;
                if (_bufferLength == 0)
                {
                    if (line.WrittenCount == 0)
                    {
                        return null;
                    }

                    if (tooLarge)
                    {
                        throw new McpMessageTooLargeException(_maximumBytes);
                    }

                    return Decode(line.WrittenSpan);
                }
            }

            var remaining = _readBuffer.AsSpan(_bufferOffset, _bufferLength - _bufferOffset);
            var newlineIndex = remaining.IndexOf((byte)'\n');
            var segmentLength = newlineIndex >= 0 ? newlineIndex : remaining.Length;
            if (!tooLarge && line.WrittenCount + segmentLength <= _maximumBytes)
            {
                line.Write(remaining[..segmentLength]);
            }
            else if (line.WrittenCount + segmentLength > _maximumBytes)
            {
                tooLarge = true;
            }

            _bufferOffset += segmentLength;
            if (newlineIndex < 0)
            {
                continue;
            }

            _bufferOffset++;
            if (tooLarge)
            {
                throw new McpMessageTooLargeException(_maximumBytes);
            }

            var bytes = line.WrittenSpan;
            if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
            {
                bytes = bytes[..^1];
            }

            return Decode(bytes);
        }
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new JsonRpcProtocolException(-32700, "Parse error: input is not valid UTF-8.", data: exception.Message);
        }
    }
}
