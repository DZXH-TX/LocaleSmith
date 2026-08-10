using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using LocaleSmith.Infrastructure.Cli;

namespace LocaleSmith.Infrastructure.Tests;

public sealed class SafeCliTests
{
    [Fact]
    public void DefaultExecutableDiscoveryNeverTrustsPathEntries()
    {
        var discovered = TrustedCliExecutableDiscovery.FindInstalled();

        Assert.Empty(discovered);
    }

    [Fact]
    public void DotnetRemainsBlockedEvenWhenItsAbsolutePathIsAllowlisted()
    {
        using var sandbox = new TemporaryDirectory();
        var dotnet = GetActualDotnetExecutable();
        using var policy = new SafeCliCommandPolicy([dotnet], temporaryRoot: sandbox.Path);

        Assert.Equal(
            CliPolicyViolation.InterpreterNotAllowed,
            policy.Evaluate(new CliCommand(dotnet, ["--version"], sandbox.Path)).Violation);
        Assert.Equal(
            CliPolicyViolation.InterpreterNotAllowed,
            policy.Evaluate(new CliCommand(dotnet, ["--info"], sandbox.Path)).Violation);
    }

    [Fact]
    public void DefaultSandboxIsApplicationSpecific()
    {
        using var policy = new SafeCliCommandPolicy([GetAllowedTestExecutable()]);
        var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var baseRoot = string.IsNullOrWhiteSpace(localAppData) ? AppContext.BaseDirectory : localAppData;
        var expected = Path.GetFullPath(Path.Combine(baseRoot, "LocaleSmith", "CliSandbox"));

        Assert.Contains(expected, policy.SandboxRoots);
    }

    [Fact]
    public void DynamicAllowlistAndSandboxAreBothRequired()
    {
        using var sandbox = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        using var policy = new SafeCliCommandPolicy([GetAllowedTestExecutable()], [sandbox.Path], temporaryRoot: sandbox.Path);
        var allowed = new CliCommand(GetAllowedTestExecutableName(), ["--info"], sandbox.Path);
        var alternateExecutable = System.Environment.ProcessPath
            ?? throw new InvalidOperationException("The test host executable path is unavailable.");
        var deniedExecutable = new CliCommand(alternateExecutable, ["--help"], sandbox.Path);

        Assert.True(policy.Evaluate(allowed).IsAllowed);
        Assert.Equal(CliPolicyViolation.ExecutableNotAllowed, policy.Evaluate(deniedExecutable).Violation);
        Assert.True(policy.AddAllowedExecutable(alternateExecutable));
        Assert.True(policy.Evaluate(deniedExecutable).IsAllowed);
        Assert.True(policy.RemoveAllowedExecutable(alternateExecutable));

        using var outsidePolicy = new SafeCliCommandPolicy([GetAllowedTestExecutable()], temporaryRoot: sandbox.Path);
        var outsideCommand = new CliCommand(GetAllowedTestExecutableName(), ["--info"], outside.Path);
        Assert.Equal(CliPolicyViolation.WorkingDirectoryOutsideSandbox, outsidePolicy.Evaluate(outsideCommand).Violation);
    }

    [Fact]
    public void SandboxRootsCanBeReplacedAfterEncryptedConfigurationLoads()
    {
        using var temporaryRoot = new TemporaryDirectory();
        using var previousRoot = new TemporaryDirectory();
        using var configuredRoot = new TemporaryDirectory();
        using var policy = new SafeCliCommandPolicy(
            [GetAllowedTestExecutable()],
            [previousRoot.Path],
            temporaryRoot: temporaryRoot.Path);
        var previous = new CliCommand(GetAllowedTestExecutableName(), ["--version"], previousRoot.Path);
        var configured = new CliCommand(GetAllowedTestExecutableName(), ["--version"], configuredRoot.Path);

        Assert.True(policy.Evaluate(previous).IsAllowed);
        policy.ReplaceSandboxRoots([configuredRoot.Path]);

        Assert.Equal(CliPolicyViolation.WorkingDirectoryOutsideSandbox, policy.Evaluate(previous).Violation);
        Assert.True(policy.Evaluate(configured).IsAllowed);
        Assert.Contains(Path.GetFullPath(temporaryRoot.Path), policy.SandboxRoots);
        Assert.Contains(Path.GetFullPath(configuredRoot.Path), policy.SandboxRoots);
    }

    [Fact]
    public void AbsoluteBlacklistRulesCannotBeOverriddenByAllowlist()
    {
        using var sandbox = new TemporaryDirectory();
        using var policy = new SafeCliCommandPolicy([GetAllowedTestExecutable()], temporaryRoot: sandbox.Path);
        CliCommand[] forbidden =
        [
            new(GetAllowedTestExecutableName(), ["::"], sandbox.Path),
            new(GetAllowedTestExecutableName(), ["Format", "C:"], sandbox.Path),
            new(GetAllowedTestExecutableName(), ["reformatting-is-forbidden"], sandbox.Path),
            new(GetAllowedTestExecutableName(), ["rd", "/s", "/q", "folder"], sandbox.Path),
            new(GetAllowedTestExecutableName(), ["del", "/f", "/s", "file"], sandbox.Path),
            new(GetAllowedTestExecutableName(), [">", "nul"], sandbox.Path),
            new(GetAllowedTestExecutableName(), ["Remove-Item", "-Recurse", "-Force", "folder"], sandbox.Path),
            new(GetAllowedTestExecutableName(), ["-EncodedCommand", "ZABlAGwA"], sandbox.Path)
        ];

        foreach (var command in forbidden)
        {
            var decision = policy.Evaluate(command);
            Assert.False(decision.IsAllowed);
            Assert.Equal(CliPolicyViolation.AbsoluteBlacklistMatch, decision.Violation);
        }
    }

    [Fact]
    public void CredentialLikeArgumentsAreNeverEligibleForCliApproval()
    {
        using var sandbox = new TemporaryDirectory();
        using var policy = new SafeCliCommandPolicy([GetAllowedTestExecutable()], temporaryRoot: sandbox.Path);
        CliCommand[] commands =
        [
            new(GetAllowedTestExecutableName(), ["tool", "--api-key", "model-supplied-value"], sandbox.Path),
            new(GetAllowedTestExecutableName(), ["--token=model-supplied-value"], sandbox.Path),
            new(GetAllowedTestExecutableName(), ["/password:model-supplied-value"], sandbox.Path)
        ];

        foreach (CliCommand command in commands)
        {
            CliPolicyDecision decision = policy.Evaluate(command);
            Assert.False(decision.IsAllowed);
            Assert.Equal(CliPolicyViolation.SensitiveArgumentNotAllowed, decision.Violation);
        }
    }

    [Fact]
    public void TimeoutAndProtectedPathsAreRejected()
    {
        using var sandbox = new TemporaryDirectory();
        using var policy = new SafeCliCommandPolicy([GetAllowedTestExecutable()], temporaryRoot: sandbox.Path);
        var tooLong = new CliCommand(GetAllowedTestExecutableName(), ["--info"], sandbox.Path, TimeSpan.FromSeconds(31));
        var protectedPath = new CliCommand(GetAllowedTestExecutableName(), [@"C:\Windows\System32\drivers\etc\hosts"], sandbox.Path);

        Assert.Equal(CliPolicyViolation.TimeoutTooLong, policy.Evaluate(tooLong).Violation);
        Assert.Equal(CliPolicyViolation.ProtectedPathAccess, policy.Evaluate(protectedPath).Violation);
    }

    [Fact]
    public void ArgumentPathsAndEnvironmentExpansionCannotEscapeSandbox()
    {
        using var sandbox = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        using var policy = new SafeCliCommandPolicy([GetAllowedTestExecutable()], temporaryRoot: sandbox.Path);
        var absoluteEscape = new CliCommand(
            GetAllowedTestExecutableName(),
            [$"--output={Path.Combine(outside.Path, "result.bin")}"],
            sandbox.Path);
        var traversal = new CliCommand(GetAllowedTestExecutableName(), [@"..\escape.txt"], sandbox.Path);
        var expansion = new CliCommand(GetAllowedTestExecutableName(), ["%USERPROFILE%\\secret.txt"], sandbox.Path);

        Assert.Equal(CliPolicyViolation.PathArgumentOutsideSandbox, policy.Evaluate(absoluteEscape).Violation);
        Assert.Equal(CliPolicyViolation.PathArgumentOutsideSandbox, policy.Evaluate(traversal).Violation);
        Assert.Equal(CliPolicyViolation.EnvironmentExpansionNotAllowed, policy.Evaluate(expansion).Violation);
    }

    [Fact]
    public void RelativePathThroughSandboxReparsePointCannotEscapePolicyRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(outside.Path, "payload.dll"), "outside");
        var link = Path.Combine(sandbox.Path, "junction");
        Directory.CreateSymbolicLink(link, outside.Path);
        using var policy = new SafeCliCommandPolicy([GetAllowedTestExecutable()], temporaryRoot: sandbox.Path);
        var command = new CliCommand(GetAllowedTestExecutableName(), [@"junction\payload.dll"], sandbox.Path);

        CliPolicyDecision decision = policy.Evaluate(command);

        Assert.False(decision.IsAllowed);
        Assert.Equal(CliPolicyViolation.PathArgumentOutsideSandbox, decision.Violation);
    }

    [Fact]
    public void MalformedRelativePathFailsClosedInsteadOfEscapingPolicyEvaluation()
    {
        using var sandbox = new TemporaryDirectory();
        using var policy = new SafeCliCommandPolicy([GetAllowedTestExecutable()], temporaryRoot: sandbox.Path);
        var command = new CliCommand(GetAllowedTestExecutableName(), ["bad\0path.txt"], sandbox.Path);

        CliPolicyDecision decision = policy.Evaluate(command);

        Assert.False(decision.IsAllowed);
        Assert.Equal(CliPolicyViolation.PathArgumentOutsideSandbox, decision.Violation);
    }

    [Fact]
    public void WindowsDriveRootRelativeForwardSlashPathsCannotMasqueradeAsOptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        using var policy = new SafeCliCommandPolicy([GetAllowedTestExecutable()], temporaryRoot: sandbox.Path);
        CliCommand[] commands =
        [
            new(GetAllowedTestExecutableName(), ["/Windows/System32/drivers/etc/hosts"], sandbox.Path),
            new(GetAllowedTestExecutableName(), ["--out=/Windows/System32/result.bin"], sandbox.Path),
            new(GetAllowedTestExecutableName(), ["/out:/Program Files/result.bin"], sandbox.Path)
        ];

        foreach (CliCommand command in commands)
        {
            CliPolicyDecision decision = policy.Evaluate(command);
            Assert.False(decision.IsAllowed);
            Assert.True(
                decision.Violation is CliPolicyViolation.ProtectedPathAccess or
                    CliPolicyViolation.PathArgumentOutsideSandbox,
                $"Unexpected violation for {command.ToDisplayString()}: {decision.Violation}");
        }
    }

    [Fact]
    public void DirectShellInterpretersRemainBlockedEvenWhenAllowlisted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        using var policy = new SafeCliCommandPolicy([GetCmdExecutable()], temporaryRoot: sandbox.Path);
        var command = new CliCommand("cmd.exe", ["/c", "echo", "hello"], sandbox.Path);

        Assert.Equal(CliPolicyViolation.InterpreterNotAllowed, policy.Evaluate(command).Violation);
    }

    [Fact]
    public void ConfirmationIsBoundToCommandAndSingleUse()
    {
        using var sandbox = new TemporaryDirectory();
        var approvals = new CliApprovalService();
        var command = new CliCommand(GetAllowedTestExecutableName(), ["--version"], sandbox.Path);
        var changed = new CliCommand(GetAllowedTestExecutableName(), ["--info"], sandbox.Path);

        Assert.Throws<InvalidOperationException>(() => approvals.Issue(command, userAcknowledgedRisk: false));
        var wrongCommandToken = approvals.Issue(command, userAcknowledgedRisk: true);
        Assert.False(approvals.TryConsume(wrongCommandToken, changed));
        var token = approvals.Issue(command, userAcknowledgedRisk: true);
        Assert.True(approvals.TryConsume(token, command));
        Assert.False(approvals.TryConsume(token, command));
    }

    [Fact]
    public async Task RunnerRequiresApprovalAndWritesRedactedAudit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sandbox = new TemporaryDirectory();
        var probe = GetProbeExecutable();
        using var policy = new SafeCliCommandPolicy([probe], temporaryRoot: sandbox.Path);
        var approvals = new CliApprovalService();
        var audit = new MemoryAuditSink();
        var runner = new SafeCliRunner(policy, approvals, audit, new FixedPrivilegeContext(isElevated: false));
        var command = new CliCommand(probe, ["inspect"], sandbox.Path);

        var rejected = await runner.ExecuteAsync(command, approvalToken: string.Empty, cancellationToken);
        var token = approvals.Issue(command, userAcknowledgedRisk: true);
        var completed = await runner.ExecuteAsync(command, token, cancellationToken);

        Assert.Equal(CliExecutionStatus.Rejected, rejected.Status);
        Assert.True(
            completed.Status == CliExecutionStatus.Completed,
            $"Expected restricted execution to complete, but it returned: {completed.Reason}");
        Assert.Equal(0, completed.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(completed.StandardOutput));
        Assert.Collection(
            audit.Records,
            first => Assert.Equal(CliExecutionStatus.Rejected, first.Status),
            second => Assert.Equal(CliExecutionStatus.Started, second.Status),
            third => Assert.Equal(CliExecutionStatus.Completed, third.Status));
        Assert.Equal(audit.Records[1].CorrelationId, audit.Records[2].CorrelationId);
    }

    [Fact]
    public async Task ElevatedHostIsNeverAllowedToStartCli()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sandbox = new TemporaryDirectory();
        using var policy = new SafeCliCommandPolicy([GetAllowedTestExecutable()], temporaryRoot: sandbox.Path);
        var approvals = new CliApprovalService();
        var audit = new MemoryAuditSink();
        var runner = new SafeCliRunner(policy, approvals, audit, new FixedPrivilegeContext(isElevated: true));
        var command = new CliCommand(GetAllowedTestExecutableName(), ["--version"], sandbox.Path);
        var token = approvals.Issue(command, userAcknowledgedRisk: true);

        var result = await runner.ExecuteAsync(command, token, cancellationToken);

        Assert.Equal(CliExecutionStatus.Rejected, result.Status);
        Assert.Contains("elevated", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealRestrictedChildRunsLowIntegrityInsideJobOnPrivateDesktop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        var probe = GetProbeExecutable();
        using var policy = new SafeCliCommandPolicy([probe], temporaryRoot: sandbox.Path);
        var approvals = new CliApprovalService();
        var audit = new MemoryAuditSink();
        var runner = new SafeCliRunner(policy, approvals, audit, new FixedPrivilegeContext(isElevated: false));
        var command = new CliCommand(probe, ["inspect"], sandbox.Path, TimeSpan.FromSeconds(10));
        var token = approvals.Issue(command, userAcknowledgedRisk: true);

        var result = await runner.ExecuteAsync(command, token, TestContext.Current.CancellationToken);

        Assert.True(result.Status == CliExecutionStatus.Completed, result.Reason);
        Assert.Contains("integrity=S-1-16-4096", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("inJob=True", result.StandardOutput, StringComparison.Ordinal);
        Assert.Matches(@"desktop=LocaleSmithCli_[a-f0-9]{32}", result.StandardOutput);
        Assert.Collection(
            audit.Records,
            started => Assert.Equal(CliExecutionStatus.Started, started.Status),
            completed => Assert.Equal(CliExecutionStatus.Completed, completed.Status));
        Assert.Equal(audit.Records[0].CorrelationId, audit.Records[1].CorrelationId);
    }

    [Fact]
    public async Task RealRestrictedLauncherPreservesWindowsArgumentBoundaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        var probe = GetProbeExecutable();
        using var policy = new SafeCliCommandPolicy([probe], temporaryRoot: sandbox.Path);
        var approvals = new CliApprovalService();
        var runner = new SafeCliRunner(
            policy,
            approvals,
            new MemoryAuditSink(),
            new FixedPrivilegeContext(isElevated: false));
        var command = new CliCommand(
            probe,
            ["echo", "space value", "trailing\\", "embedded\"quote", string.Empty],
            sandbox.Path,
            TimeSpan.FromSeconds(10));
        var token = approvals.Issue(command, userAcknowledgedRisk: true);

        var result = await runner.ExecuteAsync(command, token, TestContext.Current.CancellationToken);

        Assert.True(result.Status == CliExecutionStatus.Completed, result.Reason);
        Assert.Equal("space value|trailing\\|embedded\"quote|", result.StandardOutput.TrimEnd());
    }

    [Fact]
    public async Task TimeoutTerminatesRestrictedProcessTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        var probe = GetProbeExecutable();
        using var policy = new SafeCliCommandPolicy([probe], temporaryRoot: sandbox.Path);
        var approvals = new CliApprovalService();
        var runner = new SafeCliRunner(
            policy,
            approvals,
            new MemoryAuditSink(),
            new FixedPrivilegeContext(isElevated: false));
        var command = new CliCommand(probe, ["spawn-tree"], sandbox.Path, TimeSpan.FromSeconds(1));
        var token = approvals.Issue(command, userAcknowledgedRisk: true);

        var result = await runner.ExecuteAsync(command, token, TestContext.Current.CancellationToken);

        Assert.Equal(CliExecutionStatus.TimedOut, result.Status);
        var match = Regex.Match(result.StandardOutput, @"childPid=(?<pid>\d+)", RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"The probe did not report its child process ID. Output: {result.StandardOutput}");
        var childProcessId = int.Parse(match.Groups["pid"].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(await WaitForProcessExitAsync(childProcessId));
    }

    [Fact]
    public async Task PolicySuppliedInvalidAbsolutePathFailsClosedAndIsAudited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        var missing = Path.Combine(sandbox.Path, "missing-tool.exe");
        var policy = new PermitPathPolicy(missing);
        var approvals = new CliApprovalService();
        var audit = new MemoryAuditSink();
        var runner = new SafeCliRunner(policy, approvals, audit, new FixedPrivilegeContext(isElevated: false));
        var command = new CliCommand("approved-alias", [], sandbox.Path);
        var token = approvals.Issue(command, userAcknowledgedRisk: true);

        var result = await runner.ExecuteAsync(command, token, TestContext.Current.CancellationToken);

        Assert.Equal(CliExecutionStatus.Failed, result.Status);
        Assert.Contains("failed closed", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(
            audit.Records,
            started => Assert.Equal(CliExecutionStatus.Started, started.Status),
            failed => Assert.Equal(CliExecutionStatus.Failed, failed.Status));
    }

    [Fact]
    public async Task InjectedNativeStartupFailureHasNoUnrestrictedFallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        var probe = GetProbeExecutable();
        using var policy = new SafeCliCommandPolicy([probe], temporaryRoot: sandbox.Path);
        var approvals = new CliApprovalService();
        var audit = new MemoryAuditSink();
        var launcher = new ThrowingRestrictedProcessLauncher();
        var runner = new SafeCliRunner(
            policy,
            approvals,
            audit,
            new FixedPrivilegeContext(isElevated: false),
            launcher);
        var command = new CliCommand(probe, ["inspect"], sandbox.Path);
        var token = approvals.Issue(command, userAcknowledgedRisk: true);

        var result = await runner.ExecuteAsync(command, token, TestContext.Current.CancellationToken);

        Assert.Equal(CliExecutionStatus.Failed, result.Status);
        Assert.Equal(1, launcher.StartCount);
        Assert.Contains("AssignProcessToJobObject", result.Reason, StringComparison.Ordinal);
        Assert.Collection(
            audit.Records,
            started => Assert.Equal(CliExecutionStatus.Started, started.Status),
            failed => Assert.Equal(CliExecutionStatus.Failed, failed.Status));
    }

    [Fact]
    public async Task UnavailablePreStartAuditFailsClosedBeforeLauncherRuns()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        var executable = System.Environment.ProcessPath
            ?? throw new InvalidOperationException("The test host executable path is unavailable.");
        var policy = new PermitPathPolicy(executable);
        var approvals = new CliApprovalService();
        var launcher = new ThrowingRestrictedProcessLauncher();
        var runner = new SafeCliRunner(
            policy,
            approvals,
            new ThrowingAuditSink(),
            new FixedPrivilegeContext(isElevated: false),
            launcher);
        var command = new CliCommand("approved-alias", [], sandbox.Path);
        var token = approvals.Issue(command, userAcknowledgedRisk: true);

        CliExecutionResult result = await runner.ExecuteAsync(
            command,
            token,
            TestContext.Current.CancellationToken);

        Assert.Equal(CliExecutionStatus.Failed, result.Status);
        Assert.Contains("audit log was unavailable", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, launcher.StartCount);
    }

    private static string GetProbeExecutable()
    {
        var fileName = OperatingSystem.IsWindows()
            ? "LocaleSmith.CliProbe.exe"
            : "LocaleSmith.CliProbe";
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("The CLI integration-test probe was not copied to test output.", path);
    }

    [Fact]
    public void DynamicAllowlistRejectsPathResolvedExecutableNames()
    {
        using var sandbox = new TemporaryDirectory();

        Assert.Throws<ArgumentException>(() =>
            new SafeCliCommandPolicy([GetAllowedTestExecutableName()], temporaryRoot: sandbox.Path));
    }

    private static string GetAllowedTestExecutable()
        => GetProbeExecutable();

    private static string GetAllowedTestExecutableName() =>
        Path.GetFileName(GetAllowedTestExecutable());

    private static string GetActualDotnetExecutable()
    {
        var hostName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var runtimeDirectory = new DirectoryInfo(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var runtimeRoot = runtimeDirectory.Parent?.Parent?.Parent?.FullName;
        var architectureRoot = System.Environment.GetEnvironmentVariable(
            $"DOTNET_ROOT_{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()}");
        string?[] candidates =
        [
            System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            AppContext.GetData("DOTNET_HOST_PATH") as string,
            IsDotnetHost(System.Environment.ProcessPath, hostName) ? System.Environment.ProcessPath : null,
            Path.Combine(AppContext.BaseDirectory, hostName),
            string.IsNullOrWhiteSpace(runtimeRoot) ? null : Path.Combine(runtimeRoot, hostName),
            CombineDotnetRoot(System.Environment.GetEnvironmentVariable("DOTNET_ROOT"), hostName),
            CombineDotnetRoot(architectureRoot, hostName)
        ];

        foreach (var candidate in candidates.Where(static candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            var fullPath = Path.GetFullPath(candidate!);
            if (IsDotnetHost(fullPath, hostName) && File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        throw new FileNotFoundException("The absolute dotnet host path is unavailable to the test process.");
    }

    private static string? CombineDotnetRoot(string? root, string hostName) =>
        string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, hostName);

    private static bool IsDotnetHost(string? path, string hostName) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetFileName(path), hostName, StringComparison.OrdinalIgnoreCase);

    private static string GetCmdExecutable()
    {
        var path = Path.Combine(System.Environment.SystemDirectory, "cmd.exe");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("The Windows command processor was not found.", path);
    }

    private static async Task<bool> WaitForProcessExitAsync(int processId)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private sealed class MemoryAuditSink : ICliAuditSink
    {
        public List<CliAuditRecord> Records { get; } = [];

        public Task WriteAsync(CliAuditRecord record, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAuditSink : ICliAuditSink
    {
        public Task WriteAsync(CliAuditRecord record, CancellationToken cancellationToken = default) =>
            throw new IOException("Injected audit storage failure.");
    }

    private sealed class FixedPrivilegeContext(bool isElevated) : IPrivilegeContext
    {
        public bool IsElevated { get; } = isElevated;
    }

    private sealed class PermitPathPolicy(string path) : ICliCommandPolicy
    {
        public IReadOnlySet<string> AllowedExecutables { get; } = new HashSet<string>([path]);

        public void ReplaceAllowedExecutables(IEnumerable<string> executables) =>
            throw new NotSupportedException();

        public bool AddAllowedExecutable(string executable) => throw new NotSupportedException();

        public bool RemoveAllowedExecutable(string executable) => throw new NotSupportedException();

        public CliPolicyDecision Evaluate(CliCommand command) => CliPolicyDecision.Permit(path);
    }

    private sealed class ThrowingRestrictedProcessLauncher : IRestrictedProcessLauncher
    {
        public int StartCount { get; private set; }

        public IRestrictedChildProcess Start(RestrictedProcessStartRequest request)
        {
            StartCount++;
            throw new Win32Exception(5, "Injected AssignProcessToJobObject failure.");
        }
    }
}
