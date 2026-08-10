using System.Runtime.InteropServices;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using LocaleSmith.Infrastructure.Security;

namespace LocaleSmith.Infrastructure.Tests;

public sealed class MigratingSecretStoreTests
{
    [Fact]
    public void WindowsCredentialStoreUsesLocaleSmithPrefixByDefault()
    {
        Assert.Equal("LocaleSmith", WindowsCredentialSecretStore.DefaultTargetPrefix);
    }

    [Fact]
    public async Task ResolvePrefersCurrentValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var current = new InMemorySecretStore();
        using var legacy = new InMemorySecretStore();
        await current.SetAsync("provider/key", "current".AsMemory(), cancellationToken);
        await legacy.SetAsync("provider/key", "legacy".AsMemory(), cancellationToken);
        var store = new MigratingSecretStore(current, legacy);

        using var resolved = await store.ResolveAsync("provider/key", cancellationToken);

        Assert.Equal("current", resolved?.DangerousGetString());
    }

    [Fact]
    public async Task ResolveCopiesLegacyValueIntoCurrentStoreWithoutDeletingLegacy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var current = new InMemorySecretStore();
        using var legacy = new InMemorySecretStore();
        await legacy.SetAsync("provider/key", "legacy-secret".AsMemory(), cancellationToken);
        var store = new MigratingSecretStore(current, legacy);

        using var resolved = await store.ResolveAsync("provider/key", cancellationToken);
        using var copied = await current.ResolveAsync("provider/key", cancellationToken);
        using var retained = await legacy.ResolveAsync("provider/key", cancellationToken);

        Assert.Equal("legacy-secret", resolved?.DangerousGetString());
        Assert.Equal("legacy-secret", copied?.DangerousGetString());
        Assert.Equal("legacy-secret", retained?.DangerousGetString());
    }

    [Fact]
    public async Task ResolveDoesNotOverwriteAConcurrentCurrentValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var current = new InMemorySecretStore();
        using var legacyInner = new InMemorySecretStore();
        await legacyInner.SetAsync("provider/key", "legacy".AsMemory(), cancellationToken);
        var legacy = new BlockingResolveSecretStore(legacyInner);
        var store = new MigratingSecretStore(current, legacy);

        var resolveTask = store.ResolveAsync("provider/key", cancellationToken).AsTask();
        await legacy.ResolveEntered.Task.WaitAsync(cancellationToken);
        await current.SetAsync("provider/key", "concurrent-current".AsMemory(), cancellationToken);
        legacy.AllowResolve.TrySetResult();
        using var resolved = await resolveTask;
        using var retained = await current.ResolveAsync("provider/key", cancellationToken);

        Assert.Equal("concurrent-current", resolved?.DangerousGetString());
        Assert.Equal("concurrent-current", retained?.DangerousGetString());
    }

    [Fact]
    public async Task ResolveClearsItsMigrationBuffer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var current = new CapturingSecretStore();
        using var legacy = new InMemorySecretStore();
        await legacy.SetAsync("provider/key", "legacy-secret".AsMemory(), cancellationToken);
        var store = new MigratingSecretStore(current, legacy);

        using var resolved = await store.ResolveAsync("provider/key", cancellationToken);

        Assert.Equal("legacy-secret", resolved?.DangerousGetString());
        Assert.NotNull(current.CapturedSetBuffer);
        Assert.All(current.CapturedSetBuffer, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task SetWritesOnlyToCurrentStore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var current = new InMemorySecretStore();
        using var legacy = new InMemorySecretStore();
        var store = new MigratingSecretStore(current, legacy);

        await store.SetAsync("provider/key", "new-secret".AsMemory(), cancellationToken);
        using var currentValue = await current.ResolveAsync("provider/key", cancellationToken);
        using var legacyValue = await legacy.ResolveAsync("provider/key", cancellationToken);

        Assert.Equal("new-secret", currentValue?.DangerousGetString());
        Assert.Null(legacyValue);
    }

    [Fact]
    public async Task DeleteRemovesCurrentAndLegacyValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var current = new InMemorySecretStore();
        using var legacy = new InMemorySecretStore();
        await current.SetAsync("provider/key", "current".AsMemory(), cancellationToken);
        await legacy.SetAsync("provider/key", "legacy".AsMemory(), cancellationToken);
        var store = new MigratingSecretStore(current, legacy);

        var deleted = await store.DeleteAsync("provider/key", cancellationToken);
        using var currentValue = await current.ResolveAsync("provider/key", cancellationToken);
        using var legacyValue = await legacy.ResolveAsync("provider/key", cancellationToken);

        Assert.True(deleted);
        Assert.Null(currentValue);
        Assert.Null(legacyValue);
    }

    [Fact]
    public async Task FailedLegacyDeleteCannotResurrectTheDeletedCredential()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var current = new InMemorySecretStore();
        using var legacyInner = new InMemorySecretStore();
        await legacyInner.SetAsync("provider/key", "legacy".AsMemory(), cancellationToken);
        var legacy = new DeleteFailingSecretStore(legacyInner);
        var store = new MigratingSecretStore(current, legacy);

        await Assert.ThrowsAsync<IOException>(
            () => store.DeleteAsync("provider/key", cancellationToken).AsTask());
        using var resolved = await store.ResolveAsync("provider/key", cancellationToken);
        using var retainedLegacy = await legacyInner.ResolveAsync("provider/key", cancellationToken);

        Assert.Null(resolved);
        Assert.Equal("legacy", retainedLegacy?.DangerousGetString());

        await store.SetAsync("provider/key", "replacement".AsMemory(), cancellationToken);
        using var replacement = await store.ResolveAsync("provider/key", cancellationToken);
        Assert.Equal("replacement", replacement?.DangerousGetString());
    }

    private sealed class CapturingSecretStore : ISecretStore
    {
        public char[]? CapturedSetBuffer { get; private set; }

        public ValueTask<SecretValue?> ResolveAsync(
            string reference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SecretValue?>(null);

        public ValueTask SetAsync(
            string reference,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default)
        {
            Assert.True(MemoryMarshal.TryGetArray(secret, out var segment));
            CapturedSetBuffer = segment.Array;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteAsync(
            string reference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class BlockingResolveSecretStore(InMemorySecretStore inner) : ISecretStore
    {
        public TaskCompletionSource ResolveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowResolve { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SecretValue?> ResolveAsync(
            string reference,
            CancellationToken cancellationToken = default)
        {
            ResolveEntered.TrySetResult();
            await AllowResolve.Task.WaitAsync(cancellationToken);
            return await inner.ResolveAsync(reference, cancellationToken);
        }

        public ValueTask SetAsync(
            string reference,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) =>
            inner.SetAsync(reference, secret, cancellationToken);

        public ValueTask<bool> DeleteAsync(
            string reference,
            CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(reference, cancellationToken);
    }

    private sealed class DeleteFailingSecretStore(InMemorySecretStore inner) : ISecretStore
    {
        public ValueTask<SecretValue?> ResolveAsync(
            string reference,
            CancellationToken cancellationToken = default) =>
            inner.ResolveAsync(reference, cancellationToken);

        public ValueTask SetAsync(
            string reference,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) =>
            inner.SetAsync(reference, secret, cancellationToken);

        public ValueTask<bool> DeleteAsync(
            string reference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<bool>(new IOException("Simulated legacy deletion failure."));
    }
}
