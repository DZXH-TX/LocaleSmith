using System.Text.Json;
using JaxI18n.Core.Abstractions;
using JaxI18n.Core.Models;

namespace JaxI18n.Infrastructure.Cli;

public sealed class JsonLinesCliAuditSink : ICliAuditSink, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public JsonLinesCliAuditSink(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task WriteAsync(CliAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var line = JsonSerializer.Serialize(record, SerializerOptions) + System.Environment.NewLine;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException("The audit log path does not have a parent directory.");
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(_filePath, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Dispose();
        _disposed = true;
    }
}
