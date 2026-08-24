using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LocaleSmith.Infrastructure.Security;

namespace LocaleSmith.Infrastructure.Tests;

public sealed class EncryptedConfigurationStoreTests
{
    [Fact]
    public async Task ConfigurationRoundTripsWithoutPlaintextOnDisk()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        using var secrets = new InMemorySecretStore();
        var keys = new CredentialManagerMasterKeyStore(secrets);
        var path = Path.Combine(directory.Path, "settings.enc");
        using var store = new EncryptedJsonConfigurationStore<TestConfiguration>(path, "settings", keys);
        var configuration = new TestConfiguration("http://127.0.0.1:11434", "sensitive-setting");

        await store.SaveAsync(configuration, cancellationToken);
        var onDisk = await File.ReadAllTextAsync(path, cancellationToken);
        var loaded = await store.LoadAsync(cancellationToken);

        Assert.DoesNotContain("sensitive-setting", onDisk, StringComparison.Ordinal);
        Assert.Contains("AES-256-GCM", onDisk, StringComparison.Ordinal);
        Assert.Equal(configuration, loaded);
    }

    [Fact]
    public async Task EverySaveUsesANewNonce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        using var secrets = new InMemorySecretStore();
        var keys = new CredentialManagerMasterKeyStore(secrets);
        var path = Path.Combine(directory.Path, "settings.enc");
        using var store = new EncryptedJsonConfigurationStore<TestConfiguration>(path, "settings", keys);

        await store.SaveAsync(new TestConfiguration("one", "two"), cancellationToken);
        var first = await File.ReadAllTextAsync(path, cancellationToken);
        await store.SaveAsync(new TestConfiguration("one", "two"), cancellationToken);
        var second = await File.ReadAllTextAsync(path, cancellationToken);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task SaveIfAbsentNeverOverwritesAnExistingConfiguration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        using var secrets = new InMemorySecretStore();
        var keys = new CredentialManagerMasterKeyStore(secrets);
        var path = Path.Combine(directory.Path, "settings.enc");
        using var store = new EncryptedJsonConfigurationStore<TestConfiguration>(path, "settings", keys);
        var firstConfiguration = new TestConfiguration("first", "current");

        var created = await store.SaveIfAbsentAsync(firstConfiguration, cancellationToken);
        var originalBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var replaced = await store.SaveIfAbsentAsync(
            new TestConfiguration("legacy", "must-not-win"),
            cancellationToken);
        var retainedBytes = await File.ReadAllBytesAsync(path, cancellationToken);

        Assert.True(created);
        Assert.False(replaced);
        Assert.Equal(originalBytes, retainedBytes);
        Assert.Equal(firstConfiguration, await store.LoadAsync(cancellationToken));
    }

    [Fact]
    public async Task AuthenticationTagRejectsCiphertextTampering()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        using var secrets = new InMemorySecretStore();
        var keys = new CredentialManagerMasterKeyStore(secrets);
        var path = Path.Combine(directory.Path, "settings.enc");
        using var store = new EncryptedJsonConfigurationStore<TestConfiguration>(path, "settings", keys);
        await store.SaveAsync(new TestConfiguration("one", "two"), cancellationToken);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!.AsObject();
        var ciphertext = document["ciphertext"]!.GetValue<string>();
        document["ciphertext"] = (ciphertext[0] == 'A' ? 'B' : 'A') + ciphertext[1..];
        await File.WriteAllTextAsync(path, document.ToJsonString(), cancellationToken);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.LoadAsync(cancellationToken));
    }

    [Fact]
    public async Task MasterKeyPersistsInSecretStoreAndReturnedCopiesAreIndependent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var secrets = new InMemorySecretStore();
        var firstStore = new CredentialManagerMasterKeyStore(secrets);
        var secondStore = new CredentialManagerMasterKeyStore(secrets);
        var first = await firstStore.GetOrCreateKeyAsync("settings", cancellationToken);
        var second = await secondStore.GetOrCreateKeyAsync("settings", cancellationToken);
        try
        {
            Assert.Equal(32, first.Length);
            Assert.Equal(first, second);
            Assert.NotSame(first, second);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
        }
    }

    [Fact]
    public async Task MasterKeyUsesTheInjectedChannelSpecificLockDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        using var secrets = new InMemorySecretStore();
        var lockRoot = Path.Combine(directory.Path, "LocaleSmith.Dev", "SecurityLocks");
        var keys = new CredentialManagerMasterKeyStore(secrets, lockRoot);

        var key = await keys.GetOrCreateKeyAsync("settings", cancellationToken);
        try
        {
            Assert.True(Directory.Exists(lockRoot));
            Assert.Single(Directory.EnumerateFiles(lockRoot, "*.lock"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public async Task LegacyAssociatedDataCanBeReadWhenExplicitlySelected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        using var secrets = new InMemorySecretStore();
        var keys = new CredentialManagerMasterKeyStore(secrets);
        var path = Path.Combine(directory.Path, "legacy-settings.enc");
        var configuration = new TestConfiguration("legacy-endpoint", "legacy-preference");

        using (var legacyStore = new EncryptedJsonConfigurationStore<TestConfiguration>(
                   path,
                   "settings",
                   keys,
                   associatedDataNamespace: "JaxI18n.Config"))
        {
            await legacyStore.SaveAsync(configuration, cancellationToken);
        }

        using (var defaultStore = new EncryptedJsonConfigurationStore<TestConfiguration>(path, "settings", keys))
        {
            await Assert.ThrowsAnyAsync<CryptographicException>(
                () => defaultStore.LoadAsync(cancellationToken));
        }

        using var compatibilityStore = new EncryptedJsonConfigurationStore<TestConfiguration>(
            path,
            "settings",
            keys,
            associatedDataNamespace: "JaxI18n.Config");
        var loaded = await compatibilityStore.LoadAsync(cancellationToken);

        Assert.Equal(configuration, loaded);
        Assert.Equal(
            "LocaleSmith.Config",
            EncryptedJsonConfigurationStore<TestConfiguration>.DefaultAssociatedDataNamespace);
    }

    private sealed record TestConfiguration(string OllamaEndpoint, string Preference);
}
