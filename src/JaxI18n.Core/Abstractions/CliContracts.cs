using JaxI18n.Core.Models;

namespace JaxI18n.Core.Abstractions;

public interface ICliCommandPolicy
{
    IReadOnlySet<string> AllowedExecutables { get; }

    void ReplaceAllowedExecutables(IEnumerable<string> executables);

    bool AddAllowedExecutable(string executable);

    bool RemoveAllowedExecutable(string executable);

    CliPolicyDecision Evaluate(CliCommand command);
}

public interface ICliSandboxRootManager
{
    IReadOnlySet<string> SandboxRoots { get; }

    void ReplaceSandboxRoots(IEnumerable<string> sandboxRoots);
}

public interface ICliApprovalService
{
    string Issue(CliCommand command, bool userAcknowledgedRisk);

    bool TryConsume(string token, CliCommand command);
}

public interface ICliAuditSink
{
    Task WriteAsync(CliAuditRecord record, CancellationToken cancellationToken = default);
}

public interface ICliRunner
{
    Task<CliExecutionResult> ExecuteAsync(
        CliCommand command,
        string approvalToken,
        CancellationToken cancellationToken = default);
}

public interface IPrivilegeContext
{
    bool IsElevated { get; }
}
