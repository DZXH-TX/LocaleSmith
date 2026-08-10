using CommunityToolkit.Mvvm.Input;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Presentation.ViewModels;

public sealed class CliConfirmationViewModel : ViewModelBase, IDisposable
{
    private const int OutputPreviewLength = 4096;
    private readonly CliCommand _command;
    private readonly ICliCommandPolicy _policy;
    private readonly ICliApprovalService _approvalService;
    private readonly ICliRunner _runner;
    private readonly LocaleSmith.Presentation.Abstractions.IUiTextProvider _text;
    private readonly CancellationTokenSource _executionCancellation = new();
    private CliPolicyDecision _policyDecision;
    private bool _riskAcknowledged;
    private bool _isCancelled = true;
    private CliExecutionResult? _result;
    private bool _showFullOutput;
    private bool _disposed;

    public CliConfirmationViewModel(
        CliCommand command,
        ICliCommandPolicy policy,
        ICliApprovalService approvalService,
        ICliRunner runner,
        TerminalEnvironmentContext terminalEnvironment,
        string auditLogPath,
        LocaleSmith.Presentation.Abstractions.IUiTextProvider? text = null)
    {
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _approvalService = approvalService ?? throw new ArgumentNullException(nameof(approvalService));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _text = text ?? LocaleSmith.Presentation.Abstractions.FallbackUiTextProvider.Instance;
        ArgumentNullException.ThrowIfNull(terminalEnvironment);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditLogPath);

        _policyDecision = _policy.Evaluate(_command);
        ShellDescription = terminalEnvironment.ShellVersion is null
            ? terminalEnvironment.Shell.ToString()
            : $"{terminalEnvironment.Shell} {terminalEnvironment.ShellVersion}";
        AuditLogPath = Path.GetFullPath(auditLogPath);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => CanExecute);
        CancelCommand = new RelayCommand(Cancel);
        ToggleFullOutputCommand = new RelayCommand(
            () => ShowFullOutput = !ShowFullOutput,
            () => Result is not null);
    }

    public IAsyncRelayCommand ExecuteCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand ToggleFullOutputCommand { get; }

    public string CommandDisplay => _command.ToDisplayString(redactSensitiveValues: true);

    public string Executable => _command.Executable;

    public string WorkingDirectory => Path.GetFullPath(_command.WorkingDirectory);

    public string TimeoutDescription => Text(
        "CliTimeoutFormat",
        "{0:0.###} seconds",
        _command.Timeout.TotalSeconds);

    public string ShellDescription { get; }

    public string AuditLogPath { get; }

    public CliPolicyDecision PolicyDecision
    {
        get => _policyDecision;
        private set
        {
            if (SetProperty(ref _policyDecision, value))
            {
                OnPropertyChanged(nameof(IsPolicyAllowed));
                OnPropertyChanged(nameof(IsPolicyDenied));
                OnPropertyChanged(nameof(PolicyStatus));
                OnPropertyChanged(nameof(CanExecute));
                ExecuteCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsPolicyAllowed => PolicyDecision.IsAllowed;

    public bool IsPolicyDenied => !PolicyDecision.IsAllowed;

    public string PolicyStatus => PolicyDecision.IsAllowed
        ? Text(
            "CliPolicyAllowedDynamic",
            "Allowed by the current dynamic allowlist and sandbox policy.")
        : PolicyViolationText(PolicyDecision.Violation);

    public bool RiskAcknowledged
    {
        get => _riskAcknowledged;
        set
        {
            if (SetProperty(ref _riskAcknowledged, value))
            {
                OnPropertyChanged(nameof(CanExecute));
                ExecuteCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanExecute => IsPolicyAllowed && RiskAcknowledged && !IsBusy && Result is null;

    /// <summary>The initial value is true so closing, Escape, or no response always means cancel.</summary>
    public bool IsCancelled
    {
        get => _isCancelled;
        private set => SetProperty(ref _isCancelled, value);
    }

    public CliExecutionResult? Result
    {
        get => _result;
        private set
        {
            if (SetProperty(ref _result, value))
            {
                OnPropertyChanged(nameof(HasResult));
                OnPropertyChanged(nameof(ResultSummary));
                OnPropertyChanged(nameof(StandardOutput));
                OnPropertyChanged(nameof(StandardError));
                OnPropertyChanged(nameof(ResultTechnicalReason));
                OnPropertyChanged(nameof(HasResultTechnicalReason));
                ToggleFullOutputCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanExecute));
                ExecuteCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasResult => Result is not null;

    public string ResultSummary => Result is null
        ? string.Empty
        : Text(
            "CliResultSummaryFormat",
            "{0}; exit code {1}; {2:0} ms",
            ExecutionStatusText(Result.Status),
            Result.ExitCode?.ToString(System.Globalization.CultureInfo.CurrentCulture)
                ?? Text("CliExitCodeUnavailable", "n/a"),
            Result.Duration.TotalMilliseconds);

    public string ResultTechnicalReason => Result?.Reason ?? string.Empty;

    public bool HasResultTechnicalReason => !string.IsNullOrWhiteSpace(ResultTechnicalReason);

    public bool ShowFullOutput
    {
        get => _showFullOutput;
        set
        {
            if (SetProperty(ref _showFullOutput, value))
            {
                OnPropertyChanged(nameof(StandardOutput));
                OnPropertyChanged(nameof(StandardError));
            }
        }
    }

    public string StandardOutput => FormatOutput(Result?.StandardOutput);

    public string StandardError => FormatOutput(Result?.StandardError);

    public async Task ExecuteAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PolicyDecision = _policy.Evaluate(_command);
        if (!CanExecute)
        {
            return;
        }

        IsBusy = true;
        IsCancelled = false;
        StatusMessage = Text("CliExecuting", "Executing as a low-integrity restricted process…");
        ErrorMessage = null;
        OnPropertyChanged(nameof(CanExecute));
        ExecuteCommand.NotifyCanExecuteChanged();
        try
        {
            var approvalToken = _approvalService.Issue(_command, RiskAcknowledged);
            Result = await _runner
                .ExecuteAsync(_command, approvalToken, _executionCancellation.Token)
                .ConfigureAwait(true);
            StatusMessage = Result.Status switch
            {
                CliExecutionStatus.Completed => Text(
                    "CliCompleted",
                    "Command finished. The audit record was written."),
                CliExecutionStatus.TimedOut => Text(
                    "CliTimedOut",
                    "Command timed out and its process tree was terminated."),
                CliExecutionStatus.Rejected => Text(
                    "CliRejected",
                    "Command was rejected. No process was started."),
                _ => Text(
                    "CliFailed",
                    "Command failed. Review the redacted output and audit record.")
            };
            if (Result.Status is CliExecutionStatus.Rejected or CliExecutionStatus.Failed)
            {
                ErrorMessage = Result.Status == CliExecutionStatus.Rejected
                    ? Text("CliRejectedSummary", "The command was rejected before execution.")
                    : Text("CliFailedSummary", "The command failed. Expand the result for technical details.");
            }
        }
        catch (OperationCanceledException)
        {
            IsCancelled = true;
            StatusMessage = Text("CliCancelled", "Execution cancelled.");
        }
        catch (Exception)
        {
            ErrorMessage = Text(
                "CliExecutionFailed",
                "Command execution failed before a safe result was available. No additional process was started.");
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanExecute));
            ExecuteCommand.NotifyCanExecuteChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _executionCancellation.Dispose();
        _disposed = true;
    }

    private void Cancel()
    {
        IsCancelled = true;
        if (IsBusy)
        {
            _executionCancellation.Cancel();
        }
    }

    private string FormatOutput(string? output)
    {
        if (string.IsNullOrEmpty(output) || ShowFullOutput || output.Length <= OutputPreviewLength)
        {
            return output ?? string.Empty;
        }

        return string.Concat(
            output.AsSpan(0, OutputPreviewLength),
            "\n",
            Text("CliOutputPreviewTruncated", "[Preview truncated; expand to inspect]"));
    }

    private string PolicyViolationText(CliPolicyViolation violation) => violation switch
    {
        CliPolicyViolation.ExecutableNotAllowed => Text("CliPolicyExecutableNotAllowed", "The executable is not on the dynamic allowlist."),
        CliPolicyViolation.InterpreterNotAllowed => Text("CliPolicyInterpreterNotAllowed", "Direct shell or interpreter execution is not allowed."),
        CliPolicyViolation.AbsoluteBlacklistMatch => Text("CliPolicyAbsoluteBlacklistMatch", "The command contains an absolutely prohibited pattern."),
        CliPolicyViolation.WorkingDirectoryOutsideSandbox => Text("CliPolicyWorkingDirectoryOutsideSandbox", "The working directory is outside the allowed sandbox roots."),
        CliPolicyViolation.PathArgumentOutsideSandbox => Text("CliPolicyPathArgumentOutsideSandbox", "A path argument escapes the allowed sandbox roots."),
        CliPolicyViolation.ProtectedPathAccess => Text("CliPolicyProtectedPathAccess", "The command attempts to access a protected system path."),
        CliPolicyViolation.TimeoutTooLong => Text("CliPolicyTimeoutTooLong", "The requested timeout exceeds the security limit."),
        CliPolicyViolation.ShellSyntaxNotAllowed => Text("CliPolicyShellSyntaxNotAllowed", "Shell operators or redirection syntax are not allowed."),
        CliPolicyViolation.EnvironmentExpansionNotAllowed => Text("CliPolicyEnvironmentExpansionNotAllowed", "Environment-variable expansion is not allowed in command arguments."),
        CliPolicyViolation.WorkingDirectoryMissing => Text("CliPolicyWorkingDirectoryMissing", "The sandbox working directory does not exist."),
        CliPolicyViolation.SensitiveArgumentNotAllowed => Text("CliPolicySensitiveArgumentNotAllowed", "Credential-like arguments are not allowed in model-authored commands."),
        _ => Text("CliPolicyRejected", "Rejected by command policy.")
    };

    private string ExecutionStatusText(CliExecutionStatus status) => status switch
    {
        CliExecutionStatus.Completed => Text("CliResultStatusCompleted", "Completed"),
        CliExecutionStatus.TimedOut => Text("CliResultStatusTimedOut", "Timed out"),
        CliExecutionStatus.Rejected => Text("CliResultStatusRejected", "Rejected"),
        CliExecutionStatus.Failed => Text("CliResultStatusFailed", "Failed"),
        _ => Text("CliResultStatusUnknown", "Unknown")
    };

    private string Text(string key, string fallback, params object?[] arguments) =>
        _text.GetText(key, fallback, arguments);
}
