using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Models;

namespace LocaleSmith.App.Services;

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
            var path = GetPath(normalized, normalized.PackageIdentity);
            var legacyPath = GetPath(normalized, normalized.LegacyPackageIdentity);
            var sourcePath = File.Exists(path)
                ? path
                : File.Exists(legacyPath)
                    ? legacyPath
                    : null;
            if (sourcePath is null)
            {
                return TranslationMemorySnapshot.Empty(normalized);
            }

            await using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var snapshot = await JsonSerializer
                .DeserializeAsync<TranslationMemorySnapshot>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("The translation-memory snapshot is empty.");
            if (!string.Equals(sourcePath, path, StringComparison.OrdinalIgnoreCase))
            {
                await CopyFileAtomicallyAsync(sourcePath, path, cancellationToken).ConfigureAwait(false);
            }

            return snapshot;
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
        var normalizedKey = snapshot.Key.Normalize();
        var path = GetPath(normalizedKey, normalizedKey.PackageIdentity);
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

    private string GetPath(TranslationMemoryKey key, string packageIdentity)
    {
        var identity = $"{packageIdentity}\0{key.TargetLanguage}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(_directory, $"{hash}.json");
    }

    private static async Task CopyFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The translation-memory path has no parent directory."));
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.migrating";
        try
        {
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // Another process completed the same idempotent migration first.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Dispose() => _gate.Dispose();
}
