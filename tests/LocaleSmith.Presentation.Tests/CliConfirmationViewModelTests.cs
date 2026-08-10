using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.Presentation.Tests;

public sealed class CliConfirmationViewModelTests
{
    [Fact]
    public void DefaultsToCancelAndRedactsSensitiveArguments()
    {
        var command = new CliCommand(
            "dotnet",
            ["tool", "--api-key", "plaintext"],
            Path.GetTempPath());
        using var viewModel = Create(command, CliPolicyDecision.Permit("dotnet"), out _);

        Assert.True(viewModel.IsCancelled);
        Assert.False(viewModel.CanExecute);
        Assert.DoesNotContain("plaintext", viewModel.CommandDisplay, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", viewModel.CommandDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeniedPolicyCannotBeBypassedByAcknowledgement()
    {
        var command = new CliCommand("dotnet", ["Format", "C:"], Path.GetTempPath());
        using var viewModel = Create(
            command,
            CliPolicyDecision.Deny(CliPolicyViolation.AbsoluteBlacklistMatch, "Absolute blacklist"),
            out var runner);
        viewModel.RiskAcknowledged = true;

        await viewModel.ExecuteAsync();

        Assert.False(viewModel.CanExecute);
        Assert.Equal(0, runner.ExecutionCount);
        Assert.True(viewModel.IsPolicyDenied);
    }

    [Fact]
    public async Task AllowedCommandRequiresAcknowledgementAndRecordsResult()
    {
        var command = new CliCommand("dotnet", ["--version"], Path.GetTempPath());
        using var viewModel = Create(command, CliPolicyDecision.Permit("dotnet"), out var runner);
        await viewModel.ExecuteAsync();
        Assert.Equal(0, runner.ExecutionCount);

        viewModel.RiskAcknowledged = true;
        await viewModel.ExecuteAsync();

        Assert.Equal(1, runner.ExecutionCount);
        Assert.False(viewModel.IsCancelled);
        Assert.NotNull(viewModel.Result);
        Assert.Contains("exit code 0", viewModel.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixedCliPhrasesAndPolicyViolationUseLocalizedText()
    {
        var command = new CliCommand(
            "dotnet",
            ["--version"],
            Path.GetTempPath(),
            TimeSpan.FromSeconds(12.5));
        var text = new DictionaryTextProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CliTimeoutFormat"] = "{0:0.###} 秒",
            ["CliPolicyAllowedDynamic"] = "策略允许",
            ["CliResultStatusCompleted"] = "已完成",
            ["CliResultSummaryFormat"] = "{0}；退出代码 {1}；耗时 {2:0} 毫秒"
        });
        using var viewModel = Create(command, CliPolicyDecision.Permit("dotnet"), out _, text);

        Assert.Equal("12.5 秒", viewModel.TimeoutDescription);
        viewModel.RiskAcknowledged = true;
        await viewModel.ExecuteAsync();
        Assert.Equal("已完成；退出代码 0；耗时 4 毫秒", viewModel.ResultSummary);

        var deniedText = new DictionaryTextProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CliPolicyAbsoluteBlacklistMatch"] = "命中绝对禁止模式"
        });
        using var denied = Create(
            command,
            CliPolicyDecision.Deny(CliPolicyViolation.AbsoluteBlacklistMatch, "English backend reason"),
            out _,
            deniedText);
        Assert.Equal("命中绝对禁止模式", denied.PolicyStatus);
        Assert.DoesNotContain("English backend reason", denied.PolicyStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerFailureUsesGenericMessageWithoutLeakingExceptionText()
    {
        var command = new CliCommand("dotnet", ["--version"], Path.GetTempPath());
        var policy = new FixedPolicy(CliPolicyDecision.Permit("dotnet"));
        using var viewModel = new CliConfirmationViewModel(
            command,
            policy,
            new RecordingApprovalService(),
            new ThrowingRunner(),
            new TerminalEnvironmentContext(
                "Windows",
                "10",
                TerminalShellKind.PowerShellCore,
                "7.5",
                Path.GetTempPath(),
                new Dictionary<string, string>()),
            Path.Combine(Path.GetTempPath(), "localesmith-cli-audit.jsonl"));
        viewModel.RiskAcknowledged = true;

        await viewModel.ExecuteAsync();

        Assert.Contains("safe result", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    private static CliConfirmationViewModel Create(
        CliCommand command,
        CliPolicyDecision decision,
        out RecordingRunner runner,
        IUiTextProvider? text = null)
    {
        var policy = new FixedPolicy(decision);
        var approvals = new RecordingApprovalService();
        runner = new RecordingRunner();
        return new CliConfirmationViewModel(
            command,
            policy,
            approvals,
            runner,
            new TerminalEnvironmentContext(
                "Windows",
                "10",
                TerminalShellKind.PowerShellCore,
                "7.5",
                Path.GetTempPath(),
                new Dictionary<string, string>()),
            Path.Combine(Path.GetTempPath(), "localesmith-cli-audit.jsonl"),
            text);
    }

    private sealed class FixedPolicy(CliPolicyDecision decision) : ICliCommandPolicy
    {
        public IReadOnlySet<string> AllowedExecutables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dotnet"
        };

        public void ReplaceAllowedExecutables(IEnumerable<string> executables)
        {
        }

        public bool AddAllowedExecutable(string executable) => false;

        public bool RemoveAllowedExecutable(string executable) => false;

        public CliPolicyDecision Evaluate(CliCommand command) => decision;
    }

    private sealed class RecordingApprovalService : ICliApprovalService
    {
        public string Issue(CliCommand command, bool userAcknowledgedRisk)
        {
            if (!userAcknowledgedRisk)
            {
                throw new InvalidOperationException();
            }

            return "single-use-token";
        }

        public bool TryConsume(string token, CliCommand command) => true;
    }

    private sealed class RecordingRunner : ICliRunner
    {
        public int ExecutionCount { get; private set; }

        public Task<CliExecutionResult> ExecuteAsync(
            CliCommand command,
            string approvalToken,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return Task.FromResult(new CliExecutionResult(
                CliExecutionStatus.Completed,
                0,
                "ok",
                string.Empty,
                TimeSpan.FromMilliseconds(4)));
        }
    }

    private sealed class ThrowingRunner : ICliRunner
    {
        public Task<CliExecutionResult> ExecuteAsync(
            CliCommand command,
            string approvalToken,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secret-token must never reach the UI");
    }

    private sealed class DictionaryTextProvider(IReadOnlyDictionary<string, string> values) : IUiTextProvider
    {
        public string GetText(string key, string fallback, params object?[] arguments)
        {
            var template = values.TryGetValue(key, out var value) ? value : fallback;
            return arguments.Length == 0
                ? template
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, template, arguments);
        }
    }
}
