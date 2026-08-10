using System.Text.Json;

namespace JaxI18n.Archive;

internal sealed class TransactionJournal
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly Guid _jobId;

    public TransactionJournal(Guid jobId, string logPath)
    {
        _jobId = jobId;
        LogPath = logPath;
        using var stream = new FileStream(
            logPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
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
            File.AppendAllText(LogPath, line);
        }
    }

    private sealed record TransactionJournalEntry(
        DateTimeOffset TimestampUtc,
        Guid JobId,
        string Operation,
        string Status,
        string? Detail);
}
