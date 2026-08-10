using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JaxI18n.Core.Abstractions;

namespace JaxI18n.Infrastructure.Security;

public sealed class EncryptedJsonConfigurationStore<TConfiguration> : IConfigurationStore<TConfiguration>, IDisposable
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const long MaximumFileLength = 16 * 1024 * 1024;
    private readonly string _filePath;
    private readonly string _purpose;
    private readonly IMasterKeyStore _masterKeyStore;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public EncryptedJsonConfigurationStore(
        string filePath,
        string purpose,
        IMasterKeyStore masterKeyStore,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        _filePath = Path.GetFullPath(filePath);
        _purpose = purpose;
        _masterKeyStore = masterKeyStore ?? throw new ArgumentNullException(nameof(masterKeyStore));
        _serializerOptions = serializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public async Task<TConfiguration?> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return default;
            }

            var info = new FileInfo(_filePath);
            if (info.Length > MaximumFileLength)
            {
                throw new InvalidDataException("The encrypted configuration file exceeds the size limit.");
            }

            var envelopeBytes = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
            byte[]? plaintext = null;
            byte[]? key = null;
            try
            {
                var envelope = JsonSerializer.Deserialize<EncryptionEnvelope>(envelopeBytes, _serializerOptions)
                    ?? throw new InvalidDataException("The encrypted configuration envelope is empty.");
                ValidateEnvelope(envelope);
                var nonce = Convert.FromBase64String(envelope.Nonce);
                var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
                var tag = Convert.FromBase64String(envelope.Tag);
                if (nonce.Length != NonceLength || tag.Length != TagLength)
                {
                    throw new InvalidDataException("The encrypted configuration nonce or authentication tag has an invalid length.");
                }

                plaintext = new byte[ciphertext.Length];
                key = await _masterKeyStore.GetOrCreateKeyAsync(_purpose, cancellationToken).ConfigureAwait(false);
                ValidateKey(key);
                using (var aes = new AesGcm(key, TagLength))
                {
                    aes.Decrypt(nonce, ciphertext, tag, plaintext, GetAssociatedData());
                }

                return JsonSerializer.Deserialize<TConfiguration>(plaintext, _serializerOptions)
                    ?? throw new InvalidDataException("The decrypted configuration is empty.");
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("The encrypted configuration envelope contains invalid Base64.", exception);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The encrypted configuration contains invalid JSON.", exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelopeBytes);
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }

                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(TConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(configuration, _serializerOptions);
            var ciphertext = new byte[plaintext.Length];
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var tag = new byte[TagLength];
            byte[]? key = null;
            try
            {
                key = await _masterKeyStore.GetOrCreateKeyAsync(_purpose, cancellationToken).ConfigureAwait(false);
                ValidateKey(key);
                using (var aes = new AesGcm(key, TagLength))
                {
                    aes.Encrypt(nonce, plaintext, ciphertext, tag, GetAssociatedData());
                }

                var envelope = new EncryptionEnvelope(
                    1,
                    "AES-256-GCM",
                    Convert.ToBase64String(nonce),
                    Convert.ToBase64String(ciphertext),
                    Convert.ToBase64String(tag));
                var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, _serializerOptions);
                try
                {
                    await WriteAtomicallyAsync(envelopeBytes, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(envelopeBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(tag);
                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Dispose();
        _disposed = true;
    }

    private byte[] GetAssociatedData() => Encoding.UTF8.GetBytes($"JaxI18n.Config|v1|{_purpose}");

    private async Task WriteAtomicallyAsync(byte[] content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The configuration path does not have a parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _filePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateEnvelope(EncryptionEnvelope envelope)
    {
        if (envelope.Version != 1 || !string.Equals(envelope.Algorithm, "AES-256-GCM", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The encrypted configuration envelope uses an unsupported version or algorithm.");
        }
    }

    private static void ValidateKey(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new CryptographicException("AES-256-GCM requires a 32-byte master key.");
        }
    }

    private sealed record EncryptionEnvelope(
        int Version,
        string Algorithm,
        string Nonce,
        string Ciphertext,
        string Tag);
}
