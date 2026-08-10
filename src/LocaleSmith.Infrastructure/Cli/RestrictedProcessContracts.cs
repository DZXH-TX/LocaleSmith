namespace LocaleSmith.Infrastructure.Cli;

internal sealed record RestrictedProcessStartRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout);

internal interface IRestrictedProcessLauncher
{
    IRestrictedChildProcess Start(RestrictedProcessStartRequest request);
}

internal interface IRestrictedChildProcess : IDisposable
{
    StreamReader StandardOutput { get; }

    StreamReader StandardError { get; }

    int? ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void TerminateTree();
}
