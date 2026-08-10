using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocaleSmith.Core.Models;

public sealed record CliCommand
{
    private static readonly Regex SensitiveOption = new(
        "(?i)(?:api[-_]?key|token|secret|password|credential)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public CliCommand(
        string executable,
        IReadOnlyList<string>? arguments,
        string workingDirectory,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (arguments?.Any(static argument => argument is null) == true)
        {
            throw new ArgumentException("Arguments cannot contain null values.", nameof(arguments));
        }

        if (timeout is { } timeoutValue && timeoutValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        Executable = executable.Trim();
        Arguments = arguments?.ToArray() ?? [];
        WorkingDirectory = workingDirectory;
        Timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string WorkingDirectory { get; }

    public TimeSpan Timeout { get; }

    /// <summary>
    /// True when an argument names or embeds credential-like material. The direct CLI path
    /// rejects these commands because a redacted value cannot be meaningfully approved by a user.
    /// </summary>
    public bool HasSensitiveArguments => Arguments.Any(SensitiveOption.IsMatch);

    public string ToDisplayString(bool redactSensitiveValues = true)
    {
        var values = new List<string> { Quote(Executable) };
        var redactNext = false;
        foreach (var argument in Arguments)
        {
            var rendered = argument;
            if (redactSensitiveValues && redactNext)
            {
                rendered = "***REDACTED***";
                redactNext = false;
            }
            else if (redactSensitiveValues)
            {
                var separator = argument.IndexOf('=');
                if (separator > 0 && SensitiveOption.IsMatch(argument.AsSpan(0, separator)))
                {
                    rendered = string.Concat(argument.AsSpan(0, separator + 1), "***REDACTED***");
                }
                else if (SensitiveOption.IsMatch(argument))
                {
                    redactNext = true;
                }
            }

            values.Add(Quote(rendered));
        }

        return string.Join(' ', values);
    }

    public string ComputeFingerprint()
    {
        var canonical = JsonSerializer.Serialize(new
        {
            Executable,
            Arguments,
            WorkingDirectory = Path.GetFullPath(WorkingDirectory),
            Timeout = Timeout.Ticks
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.All(static character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}

public enum CliPolicyViolation
{
    None,
    ExecutableNotAllowed,
    InterpreterNotAllowed,
    AbsoluteBlacklistMatch,
    WorkingDirectoryOutsideSandbox,
    PathArgumentOutsideSandbox,
    ProtectedPathAccess,
    TimeoutTooLong,
    ShellSyntaxNotAllowed,
    EnvironmentExpansionNotAllowed,
    WorkingDirectoryMissing,
    SensitiveArgumentNotAllowed
}

public sealed record CliPolicyDecision(
    bool IsAllowed,
    CliPolicyViolation Violation,
    string? Reason,
    string? ResolvedExecutable = null)
{
    public static CliPolicyDecision Permit(string resolvedExecutable) =>
        new(true, CliPolicyViolation.None, null, resolvedExecutable);

    public static CliPolicyDecision Deny(CliPolicyViolation violation, string reason) => new(false, violation, reason);
}

public enum CliExecutionStatus
{
    Started,
    Rejected,
    Completed,
    TimedOut,
    Failed
}

public sealed record CliExecutionResult(
    CliExecutionStatus Status,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    string? Reason = null);

public sealed record CliAuditRecord(
    DateTimeOffset Timestamp,
    string Command,
    string WorkingDirectory,
    CliExecutionStatus Status,
    int? ExitCode,
    TimeSpan Duration,
    string? Reason,
    string CorrelationId);
