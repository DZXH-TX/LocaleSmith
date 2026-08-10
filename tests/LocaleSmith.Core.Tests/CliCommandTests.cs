using LocaleSmith.Core.Models;

namespace LocaleSmith.Core.Tests;

public sealed class CliCommandTests
{
    [Fact]
    public void AuditDisplayRedactsCommonSecretOptions()
    {
        var command = new CliCommand(
            "tool",
            ["--api-key", "plaintext", "--token=also-plaintext", "safe"],
            Path.GetTempPath());

        var display = command.ToDisplayString();

        Assert.DoesNotContain("plaintext", display, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", display, StringComparison.Ordinal);
        Assert.Contains("safe", display, StringComparison.Ordinal);
        Assert.True(command.HasSensitiveArguments);
    }

    [Fact]
    public void ApprovalFingerprintBindsEveryCommandProperty()
    {
        var first = new CliCommand("dotnet", ["--info"], Path.GetTempPath());
        var second = new CliCommand("dotnet", ["--version"], Path.GetTempPath());

        Assert.NotEqual(first.ComputeFingerprint(), second.ComputeFingerprint());
    }

    [Fact]
    public void DisposedSecretCannotBeMaterializedAgain()
    {
        var secret = new SecretValue("value");
        secret.Dispose();

        Assert.Throws<ObjectDisposedException>(secret.DangerousGetString);
    }
}
