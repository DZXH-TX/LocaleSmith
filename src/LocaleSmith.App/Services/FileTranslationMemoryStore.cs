using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Models;

namespace LocaleSmith.App.Services;

public sealed class FileTranslationMemoryStore : ITranslationMemoryStore, IDisposable
{
    private const long MaximumSnapshotBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly string[] _legacyDirectories;
    private readonly Func<string, string, CancellationToken, Task<bool>> _promoteLegacySlotAsync;
    private readonly long _maximumSnapshotBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileTranslationMemoryStore(string directory)
        : this(directory, [], CopyFileAtomicallyAsync)
    {
    }

    public FileTranslationMemoryStore(
        string directory,
        IEnumerable<string> legacyDirectories)
        : this(directory, legacyDirectories, CopyFileAtomicallyAsync)
    {
    }

    internal FileTranslationMemoryStore(
        string directory,
        Func<string, string, CancellationToken, Task<bool>> promoteLegacySlotAsync)
        : this(directory, [], promoteLegacySlotAsync)
    {
    }

    internal FileTranslationMemoryStore(string directory, long maximumSnapshotBytes)
        : this(directory, [], CopyFileAtomicallyAsync, maximumSnapshotBytes)
    {
    }

    internal FileTranslationMemoryStore(
        string directory,
        IEnumerable<string> legacyDirectories,
        Func<string, string, CancellationToken, Task<bool>> promoteLegacySlotAsync)
        : this(directory, legacyDirectories, promoteLegacySlotAsync, MaximumSnapshotBytes)
    {
    }

    private FileTranslationMemoryStore(
        string directory,
        IEnumerable<string> legacyDirectories,
        Func<string, string, CancellationToken, Task<bool>> promoteLegacySlotAsync,
        long maximumSnapshotBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSnapshotBytes);
        _directory = Path.GetFullPath(directory);
        ArgumentNullException.ThrowIfNull(legacyDirectories);
        _legacyDirectories = legacyDirectories
            .Select(NormalizeDirectory)
            .Where(path => !PathsEqual(path, _directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _promoteLegacySlotAsync = promoteLegacySlotAsync
            ?? throw new ArgumentNullException(nameof(promoteLegacySlotAsync));
        _maximumSnapshotBytes = maximumSnapshotBytes;
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
            var path = GetPath(_directory, normalized, normalized.PackageIdentity);
            foreach (var sourcePath in FindSourcePaths(normalized, path))
            {
                TranslationMemorySnapshot? snapshot;
                try
                {
                    snapshot = await ReadSnapshotAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Translation memory is an optimization. An inaccessible current/legacy slot
                    // must not stop a package translation or hide a later valid legacy root.
                    continue;
                }

                if (snapshot is null || !SnapshotMatchesKey(snapshot, normalized))
                {
                    continue;
                }

                if (!PathsEqual(sourcePath, path))
                {
                    try
                    {
                        if (!await _promoteLegacySlotAsync(sourcePath, path, cancellationToken).ConfigureAwait(false))
                        {
                            // Another process (or an already-present corrupt current slot) won the
                            // promotion race. Prefer a valid winner; otherwise keep using this valid
                            // read-only legacy snapshot instead of hiding later legacy roots.
                            var currentSnapshot = await ReadSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                            return currentSnapshot is not null && SnapshotMatchesKey(currentSnapshot, normalized)
                                ? currentSnapshot
                                : snapshot;
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // Cache promotion is an optimization. The already-loaded legacy snapshot is
                        // usable, and a later load can retry after transient disk or ACL failures.
                    }
                }

                return snapshot;
            }

            return TranslationMemorySnapshot.Empty(normalized);
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
        var path = GetPath(_directory, normalizedKey, normalizedKey.PackageIdentity);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var temporaryPath = Path.Combine(
                _directory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var fileStream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                await using (var stream = new SizeLimitedWriteStream(fileStream, _maximumSnapshotBytes))
                {
                    await JsonSerializer
                        .SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (fileStream.Length > _maximumSnapshotBytes)
                    {
                        throw CreateSnapshotSizeException(_maximumSnapshotBytes);
                    }
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

    private IEnumerable<string> FindSourcePaths(TranslationMemoryKey key, string currentPath)
    {
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(currentPath))
        {
            uniquePaths.Add(currentPath);
            yield return currentPath;
        }

        var currentLegacyPath = GetPath(_directory, key, key.LegacyPackageIdentity);
        if (uniquePaths.Add(currentLegacyPath) && File.Exists(currentLegacyPath))
        {
            yield return currentLegacyPath;
        }

        foreach (var legacyDirectory in _legacyDirectories)
        {
            // A legacy application-data root can contain either schema when users have moved
            // data between previews. Prefer the current schema when both files are present.
            var currentSchemaPath = GetPath(legacyDirectory, key, key.PackageIdentity);
            if (uniquePaths.Add(currentSchemaPath) && File.Exists(currentSchemaPath))
            {
                yield return currentSchemaPath;
            }

            var legacySchemaPath = GetPath(legacyDirectory, key, key.LegacyPackageIdentity);
            if (uniquePaths.Add(legacySchemaPath) && File.Exists(legacySchemaPath))
            {
                yield return legacySchemaPath;
            }
        }
    }

    private static string GetPath(
        string directory,
        TranslationMemoryKey key,
        string packageIdentity)
    {
        var identity = $"{packageIdentity}\0{key.TargetLanguage}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(directory, $"{hash}.json");
    }

    private static string NormalizeDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private async Task<TranslationMemorySnapshot?> ReadSnapshotAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        file.Refresh();
        if (!file.Exists ||
            file.Length > _maximumSnapshotBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            file.LinkTarget is not null)
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            return await JsonSerializer
                .DeserializeAsync<TranslationMemorySnapshot>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool SnapshotMatchesKey(
        TranslationMemorySnapshot snapshot,
        TranslationMemoryKey normalizedKey)
    {
        if (snapshot.Key is null || snapshot.Entries is null)
        {
            return false;
        }

        foreach (var pair in snapshot.Entries)
        {
            var entry = pair.Value;
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.RelativePath) ||
                string.IsNullOrWhiteSpace(entry.SourceHash) ||
                !string.Equals(
                    pair.Key,
                    $"{entry.RelativePath}\0{entry.Key ?? string.Empty}",
                    StringComparison.Ordinal) ||
                entry.Variants is null ||
                entry.Variants.Count == 0 ||
                entry.Variants.Any(static variant =>
                    variant is null ||
                    !Enum.IsDefined(variant.Style) ||
                    string.IsNullOrWhiteSpace(variant.Text)) ||
                entry.Variants.Select(static variant => variant.Style).Distinct().Count() != entry.Variants.Count)
            {
                return false;
            }
        }

        try
        {
            return snapshot.Key.Normalize() == normalizedKey;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> CopyFileAtomicallyAsync(
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
                return true;
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // Another process completed the same idempotent migration first.
                return false;
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

    private static InvalidDataException CreateSnapshotSizeException(long maximumBytes) =>
        new($"The translation-memory snapshot exceeds the {maximumBytes}-byte limit.");

    private sealed class SizeLimitedWriteStream(Stream inner, long maximumBytes) : Stream
    {
        private long _writtenBytes;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => _writtenBytes;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value)
        {
            if (value > maximumBytes)
            {
                throw CreateSnapshotSizeException(maximumBytes);
            }

            inner.SetLength(value);
            _writtenBytes = value;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Reserve(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(buffer.Length);
            inner.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Reserve(1);
            inner.WriteByte(value);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            Reserve(count);
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Reserve(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            // The surrounding await-using statement owns the underlying FileStream.
            base.Dispose(disposing);
        }

        private void Reserve(int count)
        {
            if (count < 0 || _writtenBytes > maximumBytes - count)
            {
                throw CreateSnapshotSizeException(maximumBytes);
            }

            _writtenBytes += count;
        }
    }

    public void Dispose() => _gate.Dispose();
}
