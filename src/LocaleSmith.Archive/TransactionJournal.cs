using System.Text;
using System.Text.Json;

namespace LocaleSmith.Archive;

internal sealed class TransactionJournal : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly Guid _jobId;
    private readonly StreamWriter _writer;
    private bool _disposed;

    public TransactionJournal(Guid jobId, string logPath)
    {
        _jobId = jobId;
        LogPath = logPath;
        var stream = new FileStream(
            logPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    public string LogPath { get; }

    public void Write(string operation, string status, string? detail = null)
    {
        var entry = new TransactionJournalEntry(
            DateTimeOffset.UtcNow,
            _jobId,
            operation,
            status,
            detail);
        string line = JsonSerializer.Serialize(entry, SerializerOptions) + Environment.NewLine;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.Write(line);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }

    private sealed record TransactionJournalEntry(
        DateTimeOffset TimestampUtc,
        Guid JobId,
        string Operation,
        string Status,
        string? Detail);
}
