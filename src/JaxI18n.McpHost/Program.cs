using System.Text;
using JaxI18n.Infrastructure.Cli;
using JaxI18n.Infrastructure.Environment;
using JaxI18n.Mcp;

namespace JaxI18n.McpHost;

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

        var sandboxRoot = Path.Combine(Path.GetTempPath(), "JaxI18n", "McpSandbox");
        Directory.CreateDirectory(sandboxRoot);
        using var commandPolicy = new SafeCliCommandPolicy(
            TrustedCliExecutableDiscovery.FindInstalled(),
            [sandboxRoot],
            maximumTimeout: TimeSpan.FromSeconds(30));
        var detector = new TerminalEnvironmentDetector();
        var contextProvider = new SafeSystemPromptContextProvider(detector);
        using var server = new McpStdioServer(
            contextProvider,
            commandPolicy,
            cliRunner: null,
            new McpServerOptions { EnableCliExecution = false });

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
}
