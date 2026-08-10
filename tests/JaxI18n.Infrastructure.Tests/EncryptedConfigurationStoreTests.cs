using System.Security.Cryptography;
using System.Text.Json.Nodes;
using JaxI18n.Infrastructure.Security;

namespace JaxI18n.Infrastructure.Tests;

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

    private sealed record TestConfiguration(string OllamaEndpoint, string Preference);
}
