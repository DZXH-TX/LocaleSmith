using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.App.Services;

public sealed class CliConfirmationViewModelFactory(
    ICliCommandPolicy policy,
    ICliApprovalService approvalService,
    ICliRunner runner,
    ITerminalEnvironmentDetector terminalEnvironmentDetector,
    string auditLogPath,
    LocaleSmith.Presentation.Abstractions.IUiTextProvider text) :
    LocaleSmith.Presentation.Abstractions.ICliDiagnosticRequestFactory
{
    public Task<CliConfirmationViewModel> CreateAsync(
        string sandboxPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxPath);
        var command = new CliCommand(
            "dotnet",
            ["--version"],
            Path.GetFullPath(sandboxPath),
            TimeSpan.FromSeconds(15));
        return CreateAsync(command, cancellationToken);
    }

    public async Task<CliConfirmationViewModel> CreateAsync(
        CliCommand command,
        CancellationToken cancellationToken = default)
    {
        var environment = await terminalEnvironmentDetector
            .DetectAsync(cancellationToken)
            .ConfigureAwait(false);
        return new CliConfirmationViewModel(
            command,
            policy,
            approvalService,
            runner,
            environment,
            auditLogPath,
            text);
    }
}
