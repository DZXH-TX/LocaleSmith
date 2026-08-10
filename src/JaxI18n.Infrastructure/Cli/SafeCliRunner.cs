using System.Text;
using JaxI18n.Core.Abstractions;
using JaxI18n.Core.Models;

namespace JaxI18n.Infrastructure.Cli;

public sealed class SafeCliRunner : ICliRunner
{
    private const int MaximumCapturedCharactersPerStream = 1024 * 1024;
    private static readonly string[] ChildEnvironmentAllowlist =
    [
        "PATH", "PATHEXT", "SystemRoot", "WINDIR", "TEMP", "TMP", "COMSPEC",
        "DOTNET_ROOT", "DOTNET_ROOT_X64", "JAVA_HOME"
    ];

    private readonly ICliCommandPolicy _policy;
    private readonly ICliApprovalService _approvalService;
    private readonly ICliAuditSink _auditSink;
    private readonly IPrivilegeContext _privilegeContext;
    private readonly IRestrictedProcessLauncher _processLauncher;
    private readonly TimeProvider _timeProvider;

    public SafeCliRunner(
        ICliCommandPolicy policy,
        ICliApprovalService approvalService,
        ICliAuditSink auditSink,
        IPrivilegeContext privilegeContext,
        TimeProvider? timeProvider = null)
        : this(
            policy,
            approvalService,
            auditSink,
            privilegeContext,
            CreateDefaultLauncher(),
            timeProvider)
    {
    }

    internal SafeCliRunner(
        ICliCommandPolicy policy,
        ICliApprovalService approvalService,
        ICliAuditSink auditSink,
        IPrivilegeContext privilegeContext,
        IRestrictedProcessLauncher processLauncher,
        TimeProvider? timeProvider = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _approvalService = approvalService ?? throw new ArgumentNullException(nameof(approvalService));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        _privilegeContext = privilegeContext ?? throw new ArgumentNullException(nameof(privilegeContext));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CliExecutionResult> ExecuteAsync(
        CliCommand command,
        string approvalToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var correlationId = Guid.NewGuid().ToString("N");
        var startedAt = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        var decision = _policy.Evaluate(command);
        if (!decision.IsAllowed)
        {
            return await RejectAsync(command, correlationId, startedAt, timestamp, decision.Reason!).ConfigureAwait(false);
        }

        if (!OperatingSystem.IsWindows())
        {
            return await RejectAsync(
                command,
                correlationId,
                startedAt,
                timestamp,
                "Restricted CLI execution is available only on Windows and has no unrestricted fallback.")
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(approvalToken) || !_approvalService.TryConsume(approvalToken, command))
        {
            return await RejectAsync(
                command,
                correlationId,
                startedAt,
                timestamp,
                "A valid, unexpired, single-use user risk confirmation is required.").ConfigureAwait(false);
        }

        if (_privilegeContext.IsElevated)
        {
            return await RejectAsync(
                command,
                correlationId,
                startedAt,
                timestamp,
                "CLI execution is disabled while the host process is elevated.").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(decision.ResolvedExecutable))
        {
            return await RejectAsync(
                command,
                correlationId,
                startedAt,
                timestamp,
                "The policy did not provide a canonical executable path.").ConfigureAwait(false);
        }

        var startedAudit = CreateAuditRecord(
            command,
            correlationId,
            startedAt,
            timestamp,
            CliExecutionStatus.Started,
            exitCode: null,
            "Approved restricted command launch is about to begin.");
        Exception? startAuditFailure = await TryWriteAuditAsync(startedAudit).ConfigureAwait(false);
        if (startAuditFailure is not null)
        {
            return new CliExecutionResult(
                CliExecutionStatus.Failed,
                null,
                string.Empty,
                string.Empty,
                _timeProvider.GetElapsedTime(timestamp),
                $"The command was not started because the audit log was unavailable: {startAuditFailure.GetType().Name}.");
        }

        var request = BuildStartRequest(command, decision.ResolvedExecutable);
        IRestrictedChildProcess? process = null;
        try
        {
            process = _processLauncher.Start(request);
            var outputTask = ReadBoundedAsync(process.StandardOutput);
            var errorTask = ReadBoundedAsync(process.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(command.Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var terminationFailure = await TerminateAndWaitAsync(process).ConfigureAwait(false);
                if (terminationFailure is not null)
                {
                    process.Dispose();
                    process = null;
                    return await FinishAsync(
                        command,
                        correlationId,
                        startedAt,
                        timestamp,
                        CreateCleanupFailureResult(timestamp, terminationFailure)).ConfigureAwait(false);
                }

                var timedOut = new CliExecutionResult(
                    CliExecutionStatus.TimedOut,
                    process.ExitCode,
                    await outputTask.ConfigureAwait(false),
                    await errorTask.ConfigureAwait(false),
                    _timeProvider.GetElapsedTime(timestamp),
                    $"Command exceeded its {command.Timeout.TotalSeconds:0.###}-second timeout.");
                return await FinishAsync(command, correlationId, startedAt, timestamp, timedOut).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var terminationFailure = await TerminateAndWaitAsync(process).ConfigureAwait(false);
                CliExecutionResult cancelled;
                if (terminationFailure is null)
                {
                    cancelled = new CliExecutionResult(
                        CliExecutionStatus.Failed,
                        process.ExitCode,
                        await outputTask.ConfigureAwait(false),
                        await errorTask.ConfigureAwait(false),
                        _timeProvider.GetElapsedTime(timestamp),
                        "Execution was cancelled by the caller.");
                }
                else
                {
                    process.Dispose();
                    process = null;
                    cancelled = CreateCleanupFailureResult(timestamp, terminationFailure);
                }

                await FinishAsync(command, correlationId, startedAt, timestamp, cancelled).ConfigureAwait(false);
                throw;
            }

            // The root may have spawned descendants that inherited the pipe handles. Terminating the
            // kill-on-close job after the root exits guarantees the full tree is gone before output drains.
            var descendantCleanupFailure = await TerminateAndWaitAsync(process).ConfigureAwait(false);
            if (descendantCleanupFailure is not null)
            {
                process.Dispose();
                process = null;
                return await FinishAsync(
                    command,
                    correlationId,
                    startedAt,
                    timestamp,
                    CreateCleanupFailureResult(timestamp, descendantCleanupFailure)).ConfigureAwait(false);
            }

            var result = new CliExecutionResult(
                CliExecutionStatus.Completed,
                process.ExitCode,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false),
                _timeProvider.GetElapsedTime(timestamp));
            return await FinishAsync(command, correlationId, startedAt, timestamp, result).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Exception? terminationFailure = null;
            if (process is not null)
            {
                terminationFailure = await TerminateAndWaitAsync(process).ConfigureAwait(false);
                if (terminationFailure is not null)
                {
                    process.Dispose();
                    process = null;
                }
            }

            var reason = terminationFailure is null
                ? $"{exception.GetType().Name}: {exception.Message}"
                : $"{exception.GetType().Name}: {exception.Message}; security cleanup also failed: " +
                  $"{terminationFailure.GetType().Name}: {terminationFailure.Message}";
            var failed = new CliExecutionResult(
                CliExecutionStatus.Failed,
                process?.ExitCode,
                string.Empty,
                string.Empty,
                _timeProvider.GetElapsedTime(timestamp),
                reason);
            return await FinishAsync(command, correlationId, startedAt, timestamp, failed).ConfigureAwait(false);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static RestrictedProcessStartRequest BuildStartRequest(
        CliCommand command,
        string resolvedExecutable)
    {
        var inherited = ChildEnvironmentAllowlist
            .Select(static name => (Name: name, Value: System.Environment.GetEnvironmentVariable(name)))
            .Where(static pair => !string.IsNullOrEmpty(pair.Value))
            .ToDictionary(static pair => pair.Name, static pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
        return new RestrictedProcessStartRequest(
            resolvedExecutable,
            command.Arguments,
            Path.GetFullPath(command.WorkingDirectory),
            inherited,
            command.Timeout);
    }

    private static IRestrictedProcessLauncher CreateDefaultLauncher() => OperatingSystem.IsWindows()
        ? WindowsRestrictedProcessLauncher.Instance
        : UnsupportedRestrictedProcessLauncher.Instance;

    private async Task<CliExecutionResult> RejectAsync(
        CliCommand command,
        string correlationId,
        DateTimeOffset timestamp,
        long started,
        string reason)
    {
        var result = new CliExecutionResult(
            CliExecutionStatus.Rejected,
            null,
            string.Empty,
            string.Empty,
            _timeProvider.GetElapsedTime(started),
            reason);
        return await FinishAsync(command, correlationId, timestamp, started, result).ConfigureAwait(false);
    }

    private async Task<CliExecutionResult> FinishAsync(
        CliCommand command,
        string correlationId,
        DateTimeOffset timestamp,
        long started,
        CliExecutionResult result)
    {
        var duration = _timeProvider.GetElapsedTime(started);
        result = result with { Duration = duration };
        var audit = CreateAuditRecord(
            command,
            correlationId,
            timestamp,
            started,
            result.Status,
            result.ExitCode,
            result.Reason);
        // Audit is deliberately independent of caller cancellation so a caller cannot suppress the record.
        Exception? auditFailure = await TryWriteAuditAsync(audit).ConfigureAwait(false);
        if (auditFailure is not null)
        {
            return result with
            {
                Status = CliExecutionStatus.Failed,
                Reason = $"The terminal audit record for correlation {correlationId} could not be written " +
                    $"({auditFailure.GetType().Name}); original status was {result.Status}."
            };
        }

        return result;
    }

    private CliAuditRecord CreateAuditRecord(
        CliCommand command,
        string correlationId,
        DateTimeOffset timestamp,
        long started,
        CliExecutionStatus status,
        int? exitCode,
        string? reason) => new(
            timestamp,
            command.ToDisplayString(redactSensitiveValues: true),
            Path.GetFullPath(command.WorkingDirectory),
            status,
            exitCode,
            _timeProvider.GetElapsedTime(started),
            reason,
            correlationId);

    private async Task<Exception?> TryWriteAuditAsync(CliAuditRecord record)
    {
        try
        {
            await _auditSink.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            return exception;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var builder = new StringBuilder(Math.Min(MaximumCapturedCharactersPerStream, 4096));
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var remaining = MaximumCapturedCharactersPerStream - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        if (builder.Length == MaximumCapturedCharactersPerStream)
        {
            builder.Append("\n[output truncated]");
        }

        return builder.ToString();
    }

    private static async Task<Exception?> TerminateAndWaitAsync(IRestrictedChildProcess process)
    {
        try
        {
            process.TerminateTree();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private CliExecutionResult CreateCleanupFailureResult(long started, Exception exception) => new(
        CliExecutionStatus.Failed,
        null,
        string.Empty,
        string.Empty,
        _timeProvider.GetElapsedTime(started),
        $"Restricted process tree cleanup failed closed: {exception.GetType().Name}: {exception.Message}");

    private sealed class UnsupportedRestrictedProcessLauncher : IRestrictedProcessLauncher
    {
        public static UnsupportedRestrictedProcessLauncher Instance { get; } = new();

        public IRestrictedChildProcess Start(RestrictedProcessStartRequest request) =>
            throw new PlatformNotSupportedException(
                "Restricted CLI process creation is Windows-only and has no unrestricted fallback.");
    }
}
