using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JaxI18n.Application.Abstractions;
using JaxI18n.Application.Models;

namespace JaxI18n.App.Services;

public sealed class FileTranslationMemoryStore : ITranslationMemoryStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileTranslationMemoryStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public async Task<TranslationMemorySnapshot> LoadAsync(
        TranslationMemoryKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var normalized = key.Normalize();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(normalized);
            if (!File.Exists(path))
            {
                return TranslationMemorySnapshot.Empty(normalized);
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer
                .DeserializeAsync<TranslationMemorySnapshot>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("The translation-memory snapshot is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The translation-memory snapshot is invalid.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        TranslationMemorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var path = GetPath(snapshot.Key.Normalize());
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var temporaryPath = Path.Combine(
                _directory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer
                        .SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(TranslationMemoryKey key)
    {
        var identity = $"{key.PackageIdentity}\0{key.TargetLanguage}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(_directory, $"{hash}.json");
    }

    public void Dispose() => _gate.Dispose();
}
