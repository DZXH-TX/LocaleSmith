using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LocaleSmith.Core.Abstractions;

namespace LocaleSmith.Infrastructure.Security;

public sealed class CredentialManagerMasterKeyStore : IMasterKeyStore
{
    private const int MasterKeyLength = 32;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private readonly ISecretStore _credentialStore;

    public CredentialManagerMasterKeyStore(ISecretStore credentialStore)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    }

    public async ValueTask<byte[]> GetOrCreateKeyAsync(
        string purpose,
        CancellationToken cancellationToken = default)
    {
        var reference = GetReference(purpose);
        var gate = Locks.GetOrAdd(reference, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processGate = await SecurityOperationLock.AcquireAsync(
                "master-key",
                reference,
                cancellationToken).ConfigureAwait(false);
            using var existing = await _credentialStore.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return DecodeKey(existing);
            }

            var key = RandomNumberGenerator.GetBytes(MasterKeyLength);
            var encoded = Convert.ToBase64String(key).ToCharArray();
            try
            {
                await _credentialStore.SetAsync(reference, encoded, cancellationToken).ConfigureAwait(false);
                return key;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(encoded.AsSpan()));
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask<bool> DeleteKeyAsync(string purpose, CancellationToken cancellationToken = default) =>
        _credentialStore.DeleteAsync(GetReference(purpose), cancellationToken);

    private static string GetReference(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(purpose))).ToLowerInvariant();
        return $"master-key/{digest}";
    }

    private static byte[] DecodeKey(Core.Models.SecretValue value)
    {
        var encoded = new char[value.Length];
        var key = new byte[MasterKeyLength];
        try
        {
            value.CopyTo(encoded);
            if (!Convert.TryFromBase64Chars(encoded, key, out var bytesWritten) ||
                bytesWritten != MasterKeyLength)
            {
                throw new CryptographicException("The stored configuration master key is not valid 256-bit Base64 data.");
            }

            return key;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(encoded.AsSpan()));
        }
    }
}
