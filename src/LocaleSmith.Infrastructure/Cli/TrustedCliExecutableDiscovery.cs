namespace LocaleSmith.Infrastructure.Cli;

/// <summary>
/// Discovers the deliberately small set of executables that may be placed on
/// the application's initial CLI allowlist. User PATH entries are never trusted.
/// </summary>
public static class TrustedCliExecutableDiscovery
{
    public static IReadOnlyList<string> FindInstalled()
    {
        // No process executable is trusted by default. In particular, dotnet is a command
        // multiplexer whose SDK resolution can be influenced by global.json in a writable
        // working directory. Future tools require a dedicated, non-multiplexing policy.
        return [];
    }
}
