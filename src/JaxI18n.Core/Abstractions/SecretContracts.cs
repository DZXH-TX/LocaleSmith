using JaxI18n.Core.Models;

namespace JaxI18n.Core.Abstractions;

public interface ISecretResolver
{
    ValueTask<SecretValue?> ResolveAsync(string reference, CancellationToken cancellationToken = default);
}

public interface ISecretStore : ISecretResolver
{
    ValueTask SetAsync(
        string reference,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(string reference, CancellationToken cancellationToken = default);
}

public interface IMasterKeyStore
{
    /// <summary>Returns a new copy of the 32-byte key. The caller must clear it after use.</summary>
    ValueTask<byte[]> GetOrCreateKeyAsync(string purpose, CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteKeyAsync(string purpose, CancellationToken cancellationToken = default);
}

public interface IConfigurationStore<TConfiguration>
{
    Task<TConfiguration?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(TConfiguration configuration, CancellationToken cancellationToken = default);
}
