using System.Text.Json;
using JaxI18n.Core.Abstractions;
using JaxI18n.Core.Models;

namespace JaxI18n.Mcp.Tests;

public sealed class McpModelToolExecutorTests
{
    [Fact]
    public void ExposesOnlyProviderSafeReadAndProposalTools()
    {
        var executor = CreateExecutor(out _);

        Assert.Equal(["cli_propose", "system_context"], executor.Tools.Select(static tool => tool.Name).Order());
        Assert.DoesNotContain(executor.Tools, static tool => tool.Name.Contains("execute", StringComparison.Ordinal));
        Assert.All(executor.Tools, static tool => Assert.DoesNotContain('.', tool.Name));
    }

    [Fact]
    public async Task SystemContextIsSanitizedAndBounded()
    {
        var executor = CreateExecutor(out _, "\u001b[31mTOKEN=top-secret\u001b[0m\u0000 safe");
        ModelToolCall call = CreateCall("call-1", "system_context", "{}");

        ModelToolResult result = await executor.ExecuteAsync(call, TestContext.Current.CancellationToken);

        Assert.Equal("call-1", result.ToolCallId);
        Assert.DoesNotContain("top-secret", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", result.Content, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliProposalEvaluatesPolicyButCannotExecuteOrIssueToken()
    {
        var executor = CreateExecutor(out RecordingPolicy policy);
        ModelToolCall call = CreateCall(
            "call-2",
            "cli_propose",
            """{"executable":"dotnet","arguments":["--info"],"workingDirectory":"C:\\sandbox"}""");

        ModelToolResult result = await executor.ExecuteAsync(call, TestContext.Current.CancellationToken);

        Assert.Equal(1, policy.EvaluationCount);
        Assert.Contains("\"allowed\":true", result.Content, StringComparison.Ordinal);
        Assert.Contains("\"tokenIssued\":false", result.Content, StringComparison.Ordinal);
        Assert.Contains("nothing was executed", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownProviderToolFailsClosed()
    {
        var executor = CreateExecutor(out _);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(
                CreateCall("call-3", "cli_execute", "{}"),
                TestContext.Current.CancellationToken));

        Assert.Contains("not exposed", exception.Message, StringComparison.Ordinal);
    }

    private static McpModelToolExecutor CreateExecutor(
        out RecordingPolicy policy,
        string context = "Windows 11; PowerShell 7")
    {
        policy = new RecordingPolicy();
        return new McpModelToolExecutor(new FixedContextProvider(context), policy);
    }

    private static ModelToolCall CreateCall(string id, string name, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new ModelToolCall(id, name, document.RootElement);
    }

    private sealed class FixedContextProvider(string context) : ISystemPromptContextProvider
    {
        public ValueTask<string> BuildAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(context);
        }
    }

    private sealed class RecordingPolicy : ICliCommandPolicy
    {
        public int EvaluationCount { get; private set; }

        public IReadOnlySet<string> AllowedExecutables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void ReplaceAllowedExecutables(IEnumerable<string> executables)
        {
        }

        public bool AddAllowedExecutable(string executable) => false;

        public bool RemoveAllowedExecutable(string executable) => false;

        public CliPolicyDecision Evaluate(CliCommand command)
        {
            EvaluationCount++;
            return CliPolicyDecision.Permit(@"C:\Program Files\dotnet\dotnet.exe");
        }
    }
}
