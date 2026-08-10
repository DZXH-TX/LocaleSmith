using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LocaleSmith.Presentation.Abstractions;

namespace LocaleSmith.App.Services;

public enum TranslationLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error
}

public enum TranslationLogViewMode
{
    Debug,
    AllLevels
}

public sealed record TranslationLogSessionInfo(
    Guid JobId,
    string DisplayName,
    DateTimeOffset StartedAt,
    string DebugLogPath,
    string AllLevelsLogPath);

public sealed class TranslationLogChangedEventArgs(Guid jobId) : EventArgs
{
    public Guid JobId { get; } = jobId;
}

/// <summary>
/// Writes one durable pair of append-only log files per translation. The debug file contains
/// Debug and higher levels; the all-levels file additionally contains Trace progress updates.
/// Logging is deliberately best effort so an unavailable diagnostic directory can never stop a
/// translation job.
/// </summary>
public sealed partial class TranslationLogService : IDisposable
{
    private const int MaximumDisplayedLogBytes = 4 * 1024 * 1024;
    private const int DefaultMaximumRetainedSessions = 500;
    private const int MaximumDisplayNameHeaderBytes = 4096;
    private const int MaximumActiveLogSessions = 64;
    private const string DebugSuffix = ".debug.log";
    private const string AllLevelsSuffix = ".all.log";
    private readonly IAppConfigurationService _configurationService;
    private readonly ConcurrentDictionary<Guid, SessionWriter> _sessions = new();
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly SemaphoreSlim _writerSlots = new(2, 2);
    private readonly SemaphoreSlim _retentionGate = new(1, 1);
    private readonly int _maximumRetainedSessions;
    private int _disposeState;

    public TranslationLogService(IAppConfigurationService configurationService)
        : this(configurationService, DefaultMaximumRetainedSessions)
    {
    }

    internal TranslationLogService(
        IAppConfigurationService configurationService,
        int maximumRetainedSessions)
    {
        _configurationService = configurationService
            ?? throw new ArgumentNullException(nameof(configurationService));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetainedSessions);
        _maximumRetainedSessions = maximumRetainedSessions;
    }

    public event EventHandler<TranslationLogChangedEventArgs>? LogsChanged;

    public async Task<TranslationLogSessionInfo?> TryStartSessionAsync(
        Guid jobId,
        string sourcePath,
        string modelSourceId,
        CancellationToken cancellationToken = default)
    {
        var session = TryBeginSession(jobId, sourcePath, modelSourceId);
        if (session is null)
        {
            return null;
        }

        try
        {
            return await session.Started.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AbortSession(jobId, session);
            throw;
        }
    }

    /// <summary>
    /// Registers a session and starts its disk writer on a background worker. This method performs
    /// no configuration or filesystem I/O, so translation scheduling and progress callbacks never
    /// wait for a slow or disconnected log location.
    /// </summary>
    internal bool BeginSession(Guid jobId, string sourcePath, string modelSourceId) =>
        TryBeginSession(jobId, sourcePath, modelSourceId) is not null;

    internal bool TryWrite(
        Guid jobId,
        TranslationLogLevel level,
        string category,
        string message)
    {
        if (Volatile.Read(ref _disposeState) != 0 || !_sessions.TryGetValue(jobId, out var writer))
        {
            return false;
        }

        return writer.TryEnqueue(new LogEntry(level, FormatLine(level, category, message)));
    }

    public void CompleteSession(
        Guid jobId,
        TranslationLogLevel level,
        string message)
    {
        _ = CompleteSessionAndWaitAsync(jobId, level, message);
    }

    internal Task CompleteSessionAndWaitAsync(
        Guid jobId,
        TranslationLogLevel level,
        string message)
    {
        if (_sessions.TryGetValue(jobId, out var writer))
        {
            _ = writer.TryEnqueue(new LogEntry(level, FormatLine(level, "Session", message)));
            writer.Complete();
            return writer.Completion;
        }

        return Task.CompletedTask;
    }

    internal Task WaitForSessionCompletionAsync(Guid jobId) =>
        _sessions.TryGetValue(jobId, out var writer)
            ? writer.Completion
            : Task.CompletedTask;

    public async Task<string> GetConfiguredDirectoryAsync(
        CancellationToken cancellationToken = default)
    {
        var configuration = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var configured = string.IsNullOrWhiteSpace(configuration.LogDirectoryPath)
            ? LocaleSmith.Presentation.Models.AppConfiguration.GetDefaultLogDirectoryPath()
            : configuration.LogDirectoryPath;
        var fullPath = LocaleSmith.Presentation.Models.AppConfiguration
            .NormalizeLogDirectoryPath(configured);

        if (File.Exists(fullPath))
        {
            throw new InvalidDataException("The translation log path must be a directory.");
        }

        return fullPath;
    }

    public async Task<IReadOnlyList<TranslationLogSessionInfo>> GetSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var directory = await GetConfiguredDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var debugPaths = await Task.Run(
            () => EnumerateNewestDebugPaths(directory),
            cancellationToken).ConfigureAwait(false);
        var sessions = new List<TranslationLogSessionInfo>();
        foreach (var debugPath in debugPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryParseSession(debugPath, out var session))
            {
                sessions.Add(await AddPersistedDisplayNameAsync(session, cancellationToken).ConfigureAwait(false));
            }
        }

        return sessions
            .OrderByDescending(static session => session.StartedAt)
            .ThenByDescending(static session => session.JobId)
            .ToArray();
    }

    public async Task<string> ReadAsync(
        TranslationLogSessionInfo session,
        TranslationLogViewMode viewMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Enum.IsDefined(viewMode))
        {
            throw new ArgumentOutOfRangeException(nameof(viewMode), viewMode, "Unknown translation log view.");
        }

        var directory = await GetConfiguredDirectoryAsync(cancellationToken).ConfigureAwait(false);
        var path = viewMode == TranslationLogViewMode.Debug
            ? session.DebugLogPath
            : session.AllLevelsLogPath;
        var safePath = ValidateSessionPath(directory, path, session.JobId, viewMode);
        return await Task.Run(async () =>
        {
            if (!File.Exists(safePath))
            {
                return string.Empty;
            }

            RejectReparsePointFile(safePath);
            await using var stream = new FileStream(
                safePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var truncated = stream.Length > MaximumDisplayedLogBytes;
            if (truncated)
            {
                stream.Seek(-MaximumDisplayedLogBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: true);
            if (truncated)
            {
                _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }

            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        SessionWriter[] writers;
        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            writers = _sessions.Values.ToArray();
        }

        foreach (var writer in writers)
        {
            writer.Complete();
        }

        try
        {
            _ = Task.WhenAll(writers.Select(static writer => writer.Completion))
                .Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException exception) when (
            exception.Flatten().InnerExceptions.All(IsNonFatalLoggingFailure))
        {
            // A diagnostic shutdown failure must not prevent the application from closing.
        }

        _disposeCancellation.Cancel();
        foreach (var writer in writers.Where(static writer => !writer.Completion.IsCompleted))
        {
            writer.Cancel();
        }
    }

    private SessionWriter? TryBeginSession(
        Guid jobId,
        string sourcePath,
        string modelSourceId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("A translation log requires a non-empty job identifier.", nameof(jobId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelSourceId);

        var sourceFile = SanitizeField(Path.GetFileName(sourcePath), "package");
        var model = SanitizeField(modelSourceId, "model");
        var session = new SessionWriter(jobId);
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                session.Cancel();
                return null;
            }

            if (_sessions.Count >= MaximumActiveLogSessions || !_sessions.TryAdd(jobId, session))
            {
                session.Cancel();
                return null;
            }

            session.Start(Task.Run(
                () => RunSessionWriterAsync(session, sourceFile),
                CancellationToken.None));
        }

        _ = session.TryEnqueue(new LogEntry(
            TranslationLogLevel.Information,
            FormatLine(
                TranslationLogLevel.Information,
                "Session",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Translation started | job={jobId:N} | source={sourceFile} | model={model}"))));
        return session;
    }

    private async Task RunSessionWriterAsync(
        SessionWriter session,
        string sourceFile)
    {
        TranslationLogSessionInfo? info = null;
        PersistedSessionWriter? writer = null;
        var slotAcquired = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _disposeCancellation.Token,
            session.CancellationToken);
        try
        {
            var token = cancellation.Token;
            await _writerSlots.WaitAsync(token).ConfigureAwait(false);
            slotAcquired = true;
            var directory = await GetConfiguredDirectoryAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            token.ThrowIfCancellationRequested();

            var startedAt = DateTimeOffset.UtcNow;
            var prefix = string.Create(
                CultureInfo.InvariantCulture,
                $"{startedAt:yyyyMMdd'T'HHmmssfff'Z'}_{session.JobId:N}");
            var debugPath = Path.Combine(directory, prefix + DebugSuffix);
            var allLevelsPath = Path.Combine(directory, prefix + AllLevelsSuffix);
            info = new TranslationLogSessionInfo(
                session.JobId,
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"{sourceFile} · {startedAt.ToLocalTime():g}"),
                startedAt,
                debugPath,
                allLevelsPath);
            writer = PersistedSessionWriter.Create(info);
            session.MarkStarted(info);
            await PruneOldSessionsBestEffortAsync(directory, token).ConfigureAwait(false);

            await foreach (var entry in session.ReadAllAsync(token).ConfigureAwait(false))
            {
                var dropped = session.TakeDroppedCount();
                if (dropped > 0)
                {
                    await WritePersistedEntryAsync(
                        writer,
                        session.JobId,
                        new LogEntry(
                            TranslationLogLevel.Warning,
                            FormatLine(
                                TranslationLogLevel.Warning,
                                "Logger",
                                string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"Dropped {dropped} diagnostic entries because the log device was too slow."))),
                        token).ConfigureAwait(false);
                }

                await WritePersistedEntryAsync(writer, session.JobId, entry, token).ConfigureAwait(false);
            }

            var trailingDropped = session.TakeDroppedCount();
            if (trailingDropped > 0)
            {
                await WritePersistedEntryAsync(
                    writer,
                    session.JobId,
                    new LogEntry(
                        TranslationLogLevel.Warning,
                        FormatLine(
                            TranslationLogLevel.Warning,
                            "Logger",
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"Dropped {trailingDropped} diagnostic entries because the log device was too slow."))),
                    token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            session.MarkStarted(null);
        }
        catch (Exception exception) when (IsNonFatalLoggingFailure(exception))
        {
            session.MarkStarted(null);
        }
        finally
        {
            session.MarkStarted(null);
            try
            {
                if (writer is not null)
                {
                    await DisposeWriterBestEffortAsync(writer).ConfigureAwait(false);
                }
            }
            finally
            {
                if (slotAcquired)
                {
                    _writerSlots.Release();
                }

                _sessions.TryRemove(new KeyValuePair<Guid, SessionWriter>(session.JobId, session));
                session.Dispose();
            }
        }
    }

    private async Task WritePersistedEntryAsync(
        PersistedSessionWriter writer,
        Guid jobId,
        LogEntry entry,
        CancellationToken cancellationToken)
    {
        await writer.WriteAsync(entry.Level, entry.Line, cancellationToken).ConfigureAwait(false);
        RaiseLogsChanged(jobId);
    }

    private void AbortSession(Guid jobId, SessionWriter session)
    {
        if (_sessions.TryGetValue(jobId, out var current) && ReferenceEquals(current, session))
        {
            session.Cancel();
        }
    }

    private static async ValueTask DisposeWriterBestEffortAsync(PersistedSessionWriter writer)
    {
        try
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsNonFatalLoggingFailure(exception))
        {
            // Diagnostics must never change the outcome of a translation or application shutdown.
        }
    }

    private async Task PruneOldSessionsBestEffortAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        try
        {
            await _retentionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                PruneOldSessions(directory);
            }
            finally
            {
                _retentionGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown may skip retention; the next successfully started session will retry it.
        }
        catch (Exception exception) when (IsNonFatalLoggingFailure(exception))
        {
            // Retention is diagnostic housekeeping and must never affect a translation job.
        }
    }

    private void PruneOldSessions(string directory)
    {
        var activeJobs = _sessions.Keys.ToHashSet();
        var completedToKeep = Math.Max(0, _maximumRetainedSessions - activeJobs.Count);
        var completedSeen = 0;
        var sessions = Directory
            .EnumerateFiles(directory, "*" + DebugSuffix, SearchOption.TopDirectoryOnly)
            .Where(IsRegularFileWithoutReparsePoint)
            .Select(static path => TryParseSession(path, out var session) ? session : null)
            .Where(static session => session is not null)
            .Cast<TranslationLogSessionInfo>()
            .Where(static session => HasOwnedSessionHeader(session.DebugLogPath, session.JobId))
            .OrderByDescending(static session => session.StartedAt)
            .ThenByDescending(static session => session.JobId)
            .ToArray();

        foreach (var session in sessions)
        {
            if (activeJobs.Contains(session.JobId) || completedSeen++ < completedToKeep)
            {
                continue;
            }

            if (_sessions.ContainsKey(session.JobId))
            {
                continue;
            }

            DeleteOwnedRegularFileBestEffort(session.DebugLogPath, session.JobId);
            if (!_sessions.ContainsKey(session.JobId))
            {
                DeleteOwnedRegularFileBestEffort(session.AllLevelsLogPath, session.JobId);
            }
        }
    }

    private static void DeleteOwnedRegularFileBestEffort(string path, Guid jobId)
    {
        try
        {
            if (HasOwnedSessionHeader(path, jobId))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (IsNonFatalLoggingFailure(exception))
        {
            // A locked or access-denied historical log can be retried by a later session.
        }
    }

    private static bool HasOwnedSessionHeader(string path, Guid jobId)
    {
        try
        {
            if (!IsRegularFileWithoutReparsePoint(path))
            {
                return false;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024,
                FileOptions.SequentialScan);
            var header = GC.AllocateUninitializedArray<byte>(MaximumDisplayNameHeaderBytes);
            var bytesRead = stream.Read(header, 0, header.Length);
            var firstLineLength = Array.IndexOf(header, (byte)'\n', 0, bytesRead);
            if (firstLineLength < 0)
            {
                firstLineLength = bytesRead;
            }

            var firstLine = Encoding.UTF8.GetString(header, 0, firstLineLength);
            var marker = string.Create(
                CultureInfo.InvariantCulture,
                $"Translation started | job={jobId:N} |");
            return firstLine.Contains(marker, StringComparison.Ordinal);
        }
        catch (Exception exception) when (IsNonFatalLoggingFailure(exception))
        {
            return false;
        }
    }

    private static string FormatLine(
        TranslationLogLevel level,
        string category,
        string message)
    {
        var timestamp = DateTimeOffset.UtcNow;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{timestamp:O}] [{level}] [{SanitizeField(category, "General")}] {SanitizeMessage(message)}");
    }

    private static string SanitizeField(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var builder = new StringBuilder(Math.Min(value.Length, 160));
        foreach (var character in value)
        {
            if (builder.Length >= 160)
            {
                break;
            }

            builder.Append(char.IsControl(character) || character == '|' ? '_' : character);
        }

        return ApplyRedaction(builder.ToString().Trim());
    }

    private static string SanitizeMessage(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(Math.Min(value.Length, 1024));
        foreach (var character in value)
        {
            if (builder.Length >= 1024)
            {
                break;
            }

            builder.Append(character is '\r' or '\n' || char.IsControl(character) ? ' ' : character);
        }

        return ApplyRedaction(builder.ToString().Trim());
    }

    private static string ApplyRedaction(string value)
    {
        var sanitized = BearerSecretPattern().Replace(value, "Bearer [REDACTED]");
        sanitized = NamedSecretPattern().Replace(sanitized, "$1=[REDACTED]");
        sanitized = OpenAiStyleSecretPattern().Replace(sanitized, "[REDACTED]");
        return WindowsAbsolutePathPattern().Replace(sanitized, "[PATH]");
    }

    private static bool IsNonFatalLoggingFailure(Exception exception) =>
        exception is not OutOfMemoryException;

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerSecretPattern();

    [GeneratedRegex(
        @"(?i)\b(authorization|api[-_]?key|access[-_]?token|token)\s*[:=]\s*(?:(?:Bearer|Basic)\s+)?[^\s|,;]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecretPattern();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiStyleSecretPattern();

    [GeneratedRegex(@"(?i)\b[A-Z]:\\[^|\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsAbsolutePathPattern();

    private static bool TryParseSession(
        string debugPath,
        out TranslationLogSessionInfo session)
    {
        var fileName = Path.GetFileName(debugPath);
        if (!fileName.EndsWith(DebugSuffix, StringComparison.OrdinalIgnoreCase))
        {
            session = null!;
            return false;
        }

        var prefix = fileName[..^DebugSuffix.Length];
        var separator = prefix.LastIndexOf('_');
        if (separator <= 0 ||
            !Guid.TryParseExact(prefix[(separator + 1)..], "N", out var jobId) ||
            !DateTimeOffset.TryParseExact(
                prefix[..separator],
                "yyyyMMdd'T'HHmmssfff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var startedAt))
        {
            session = null!;
            return false;
        }

        var directory = Path.GetDirectoryName(debugPath)!;
        var allLevelsPath = Path.Combine(directory, prefix + AllLevelsSuffix);
        var displayName = string.Create(
            CultureInfo.CurrentCulture,
            $"{jobId.ToString("N", CultureInfo.InvariantCulture)[..8]} · {startedAt.ToLocalTime():g}");
        session = new TranslationLogSessionInfo(
            jobId,
            displayName,
            startedAt,
            debugPath,
            allLevelsPath);
        return true;
    }

    private string[] EnumerateNewestDebugPaths(string directory)
    {
        var comparer = Comparer<string>.Create(static (left, right) =>
        {
            var byFileName = StringComparer.OrdinalIgnoreCase.Compare(
                Path.GetFileName(left),
                Path.GetFileName(right));
            return byFileName != 0
                ? byFileName
                : StringComparer.OrdinalIgnoreCase.Compare(left, right);
        });
        var newest = new SortedSet<string>(comparer);
        foreach (var path in Directory.EnumerateFiles(
                     directory,
                     "*" + DebugSuffix,
                     SearchOption.TopDirectoryOnly))
        {
            if (!IsRegularFileWithoutReparsePoint(path))
            {
                continue;
            }

            newest.Add(path);
            if (newest.Count > _maximumRetainedSessions)
            {
                newest.Remove(newest.Min!);
            }
        }

        return newest.Reverse().ToArray();
    }

    private static async Task<TranslationLogSessionInfo> AddPersistedDisplayNameAsync(
        TranslationLogSessionInfo session,
        CancellationToken cancellationToken)
    {
        try
        {
            var firstLine = await Task.Run(() =>
            {
                RejectReparsePointFile(session.DebugLogPath);
                using var stream = new FileStream(
                    session.DebugLogPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1024,
                    FileOptions.SequentialScan);
                var header = GC.AllocateUninitializedArray<byte>(MaximumDisplayNameHeaderBytes);
                var bytesRead = stream.Read(header, 0, header.Length);
                var lineLength = Array.IndexOf(header, (byte)'\n', 0, bytesRead);
                if (lineLength < 0)
                {
                    lineLength = bytesRead;
                }

                return Encoding.UTF8.GetString(header, 0, lineLength).TrimEnd('\r');
            }, cancellationToken).ConfigureAwait(false);
            const string marker = " | source=";
            if (firstLine.Length == 0)
            {
                return session;
            }

            var start = firstLine.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return session;
            }

            start += marker.Length;
            var end = firstLine.IndexOf(" | ", start, StringComparison.Ordinal);
            var source = end < 0 ? firstLine[start..] : firstLine[start..end];
            source = SanitizeField(source, string.Empty);
            if (string.IsNullOrWhiteSpace(source))
            {
                return session;
            }

            return session with
            {
                DisplayName = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{source} · {session.StartedAt.ToLocalTime():g}")
            };
        }
        catch (Exception exception) when (IsNonFatalLoggingFailure(exception))
        {
            return session;
        }
    }

    private static string ValidateSessionPath(
        string directory,
        string path,
        Guid jobId,
        TranslationLogViewMode viewMode)
    {
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var fullPath = Path.GetFullPath(path);
        var expectedSuffix = viewMode == TranslationLogViewMode.Debug ? DebugSuffix : AllLevelsSuffix;
        var expectedJobMarker = "_" + jobId.ToString("N", CultureInfo.InvariantCulture) + expectedSuffix;
        if (!fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).EndsWith(expectedJobMarker, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected translation log is outside the configured log directory.");
        }

        return fullPath;
    }

    private static bool IsRegularFileWithoutReparsePoint(string path)
    {
        try
        {
            var file = new FileInfo(path);
            file.Refresh();
            return file.Exists &&
                (file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                file.LinkTarget is null;
        }
        catch (Exception exception) when (IsNonFatalLoggingFailure(exception))
        {
            return false;
        }
    }

    private static void RejectReparsePointFile(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LinkTarget is not null)
        {
            throw new InvalidDataException("Symbolic-link and reparse-point log files are not supported.");
        }
    }

    private void RaiseLogsChanged(Guid jobId)
    {
        var handlers = LogsChanged;
        if (handlers is null)
        {
            return;
        }

        var args = new TranslationLogChangedEventArgs(jobId);
        foreach (EventHandler<TranslationLogChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Diagnostic UI subscribers must never affect the translation pipeline.
            }
        }
    }

    private readonly record struct LogEntry(TranslationLogLevel Level, string Line);

    private sealed class SessionWriter : IDisposable
    {
        private const int MaximumPendingEntries = 512;
        private readonly Channel<LogEntry> _entries = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(MaximumPendingEntries)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<TranslationLogSessionInfo?> _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _worker = Task.CompletedTask;
        private int _completeState;
        private int _droppedCount;
        private int _disposeState;

        public SessionWriter(Guid jobId)
        {
            JobId = jobId;
        }

        public Guid JobId { get; }

        public CancellationToken CancellationToken => _cancellation.Token;

        public Task<TranslationLogSessionInfo?> Started => _started.Task;

        public Task Completion => _worker;

        public void Start(Task worker) => _worker = worker;

        public bool TryEnqueue(LogEntry entry)
        {
            if (Volatile.Read(ref _completeState) != 0)
            {
                return false;
            }

            if (_entries.Writer.TryWrite(entry))
            {
                return true;
            }

            Interlocked.Increment(ref _droppedCount);
            return false;
        }

        public IAsyncEnumerable<LogEntry> ReadAllAsync(CancellationToken cancellationToken) =>
            _entries.Reader.ReadAllAsync(cancellationToken);

        public int TakeDroppedCount() => Interlocked.Exchange(ref _droppedCount, 0);

        public void MarkStarted(TranslationLogSessionInfo? info) => _started.TrySetResult(info);

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completeState, 1) == 0)
            {
                _entries.Writer.TryComplete();
            }
        }

        public void Cancel()
        {
            Complete();
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposeState) != 0)
            {
            }

            MarkStarted(null);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) == 0)
            {
                _cancellation.Dispose();
            }
        }
    }

    private sealed class PersistedSessionWriter : IAsyncDisposable
    {
        private readonly StreamWriter _debugWriter;
        private readonly StreamWriter _allLevelsWriter;

        private PersistedSessionWriter(StreamWriter debugWriter, StreamWriter allLevelsWriter)
        {
            _debugWriter = debugWriter;
            _allLevelsWriter = allLevelsWriter;
        }

        public static PersistedSessionWriter Create(TranslationLogSessionInfo info)
        {
            StreamWriter? debugWriter = null;
            try
            {
                debugWriter = CreateWriter(info.DebugLogPath);
                var allLevelsWriter = CreateWriter(info.AllLevelsLogPath);
                return new PersistedSessionWriter(debugWriter, allLevelsWriter);
            }
            catch
            {
                if (debugWriter is not null)
                {
                    DisposeStreamWriterBestEffort(debugWriter);
                }

                throw;
            }
        }

        public async ValueTask WriteAsync(
            TranslationLogLevel level,
            string line,
            CancellationToken cancellationToken)
        {
            await _allLevelsWriter.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _allLevelsWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (level >= TranslationLogLevel.Debug)
            {
                await _debugWriter.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _debugWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _debugWriter.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _allLevelsWriter.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static StreamWriter CreateWriter(string path)
        {
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            return new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: false);
        }

        private static void DisposeStreamWriterBestEffort(StreamWriter writer)
        {
            try
            {
                writer.Dispose();
            }
            catch (Exception exception) when (IsNonFatalLoggingFailure(exception))
            {
            }
        }
    }
}
