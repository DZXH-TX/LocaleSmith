using System.Reflection;
using System.Text;
using LocaleSmith.Infrastructure.Cli;
using LocaleSmith.Infrastructure.Environment;
using LocaleSmith.Mcp;

namespace LocaleSmith.McpHost;

internal static class Program
{
    public static async Task<int> Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync("LocaleSmith MCP host requires Windows.").ConfigureAwait(false);
            return 1;
        }

        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            shutdown.Cancel();
        };

        using var commandPolicy = new SafeCliCommandPolicy(
            [],
            additionalSandboxRoots: [],
            maximumTimeout: TimeSpan.FromSeconds(30));
        var detector = new TerminalEnvironmentDetector();
        var contextProvider = new SafeSystemPromptContextProvider(detector);
        using var server = new McpStdioServer(
            contextProvider,
            commandPolicy,
            cliRunner: null,
            new McpServerOptions
            {
                EnableCliExecution = false,
                ServerVersion = GetServerVersion()
            });

        try
        {
            await server.RunAsync(
                    Console.OpenStandardInput(),
                    Console.OpenStandardOutput(),
                    shutdown.Token)
                .ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            // stdout is reserved exclusively for newline-delimited JSON-RPC frames.
            await Console.Error
                .WriteLineAsync($"LocaleSmith MCP host failed: {exception.GetType().Name}: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static string GetServerVersion()
    {
        var assembly = typeof(Program).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var metadataSeparator = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            return metadataSeparator >= 0
                ? informationalVersion[..metadataSeparator]
                : informationalVersion;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
