namespace JaxI18n.Core.Models;

public enum TerminalShellKind
{
    Unknown,
    CommandPrompt,
    WindowsPowerShell,
    PowerShellCore
}

public sealed record TerminalEnvironmentContext(
    string OperatingSystem,
    string OperatingSystemVersion,
    TerminalShellKind Shell,
    string? ShellVersion,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> EnvironmentVariables);
