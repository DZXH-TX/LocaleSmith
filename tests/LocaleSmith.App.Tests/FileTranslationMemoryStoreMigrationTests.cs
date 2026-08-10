using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocaleSmith.App.Services;
using LocaleSmith.Application.Models;
using LocaleSmith.Core.Models;

namespace LocaleSmith.App.Tests;

public sealed class FileTranslationMemoryStoreMigrationTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task LoadPromotesLegacySlotAcrossApplicationRootsWithoutDeletingLegacyFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("localesmith-memory-migration-");
        try
        {
            var legacyDirectory = Directory.CreateDirectory(Path.Combine(
                root.FullName,
                "JaxI18n",
                "translation-memory")).FullName;
            var currentDirectory = Path.Combine(
                root.FullName,
                "LocaleSmith",
                "translation-memory");
            var key = new TranslationMemoryKey(
                "example-package",
                "zh_cn",
                "source-a",
                TranslationPromptContract.CurrentVersion).Normalize();
            var entry = new TranslatedEntry(
                "assets/example/lang/en_us.json",
                "example.key",
                "source-hash",
                [new TranslationVariant(TranslationStyle.Formal, "译文")]);
            var snapshot = new TranslationMemorySnapshot(
                key,
                new Dictionary<string, TranslatedEntry>(StringComparer.Ordinal)
                {
                    [GetStableId(entry)] = entry
                });
            var legacyPath = GetPath(legacyDirectory, key.LegacyPackageIdentity, key.TargetLanguage);
            var currentPath = GetPath(currentDirectory, key.PackageIdentity, key.TargetLanguage);
            await File.WriteAllTextAsync(
                legacyPath,
                JsonSerializer.Serialize(snapshot, WebJsonOptions),
                cancellationToken);

            using var store = new FileTranslationMemoryStore(currentDirectory, [legacyDirectory]);
            var loaded = await store.LoadAsync(key, cancellationToken);

            Assert.Equal("译文", Assert.Single(Assert.Single(loaded.Entries).Value.Variants).Text);
            Assert.True(File.Exists(legacyPath));
            Assert.True(File.Exists(currentPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoadStillPromotesLegacyHashAlreadyInCurrentDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("localesmith-memory-same-root-");
        try
        {
            var (key, snapshot) = CreateSnapshot("同根缓存");
            var legacyPath = GetPath(directory.FullName, key.LegacyPackageIdentity, key.TargetLanguage);
            var currentPath = GetPath(directory.FullName, key.PackageIdentity, key.TargetLanguage);
            await File.WriteAllTextAsync(
                legacyPath,
                JsonSerializer.Serialize(snapshot, WebJsonOptions),
                cancellationToken);

            using var store = new FileTranslationMemoryStore(directory.FullName);
            var loaded = await store.LoadAsync(key, cancellationToken);

            Assert.Equal("同根缓存", Assert.Single(Assert.Single(loaded.Entries).Value.Variants).Text);
            Assert.True(File.Exists(legacyPath));
            Assert.True(File.Exists(currentPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoadFindsCurrentHashInLaterReadOnlyLegacyRoot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("localesmith-memory-multiple-roots-");
        try
        {
            var missingLegacyDirectory = Path.Combine(root.FullName, "missing", "translation-memory");
            var legacyDirectory = Directory.CreateDirectory(Path.Combine(
                root.FullName,
                "JaxI18n",
                "translation-memory")).FullName;
            var currentDirectory = Path.Combine(
                root.FullName,
                "LocaleSmith",
                "translation-memory");
            var (key, snapshot) = CreateSnapshot("跨根缓存");
            var legacyPath = GetPath(legacyDirectory, key.PackageIdentity, key.TargetLanguage);
            var currentPath = GetPath(currentDirectory, key.PackageIdentity, key.TargetLanguage);
            await File.WriteAllTextAsync(
                legacyPath,
                JsonSerializer.Serialize(snapshot, WebJsonOptions),
                cancellationToken);

            using var store = new FileTranslationMemoryStore(
                currentDirectory,
                [missingLegacyDirectory, legacyDirectory]);
            var loaded = await store.LoadAsync(key, cancellationToken);

            Assert.Equal("跨根缓存", Assert.Single(Assert.Single(loaded.Entries).Value.Variants).Text);
            Assert.True(File.Exists(legacyPath));
            Assert.True(File.Exists(currentPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoadSkipsCorruptEarlierLegacyRootAndUsesLaterValidSnapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("localesmith-memory-corrupt-first-root-");
        try
        {
            var corruptDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "legacy-a")).FullName;
            var validDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "legacy-b")).FullName;
            var currentDirectory = Path.Combine(root.FullName, "current");
            var (key, snapshot) = CreateSnapshot("later-valid-cache");
            var corruptPath = GetPath(corruptDirectory, key.PackageIdentity, key.TargetLanguage);
            var validPath = GetPath(validDirectory, key.PackageIdentity, key.TargetLanguage);
            await File.WriteAllTextAsync(corruptPath, "{not-json", cancellationToken);
            await File.WriteAllTextAsync(
                validPath,
                JsonSerializer.Serialize(snapshot, WebJsonOptions),
                cancellationToken);
            using var store = new FileTranslationMemoryStore(
                currentDirectory,
                [corruptDirectory, validDirectory]);

            var loaded = await store.LoadAsync(key, cancellationToken);

            Assert.Equal("later-valid-cache", Assert.Single(Assert.Single(loaded.Entries).Value.Variants).Text);
            Assert.True(File.Exists(GetPath(currentDirectory, key.PackageIdentity, key.TargetLanguage)));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoadReturnsLegacySnapshotWhenBestEffortPromotionFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("localesmith-memory-promotion-failure-");
        try
        {
            var legacyDirectory = Directory.CreateDirectory(Path.Combine(
                root.FullName,
                "JaxI18n",
                "translation-memory")).FullName;
            var currentDirectory = Path.Combine(
                root.FullName,
                "LocaleSmith",
                "translation-memory");
            var (key, legacySnapshot) = CreateSnapshot("旧缓存");
            var legacyPath = GetPath(legacyDirectory, key.LegacyPackageIdentity, key.TargetLanguage);
            var currentPath = GetPath(currentDirectory, key.PackageIdentity, key.TargetLanguage);
            await File.WriteAllTextAsync(
                legacyPath,
                JsonSerializer.Serialize(legacySnapshot, WebJsonOptions),
                cancellationToken);

            using var store = new FileTranslationMemoryStore(
                currentDirectory,
                [legacyDirectory],
                static (_, _, _) => Task.FromException<bool>(new IOException("simulated disk failure")));
            var loaded = await store.LoadAsync(key, cancellationToken);

            Assert.Equal("旧缓存", Assert.Single(Assert.Single(loaded.Entries).Value.Variants).Text);
            Assert.True(File.Exists(legacyPath));
            Assert.False(File.Exists(currentPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoadUsesCurrentSnapshotWhenAnotherProcessWinsPromotionRace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("localesmith-memory-promotion-race-");
        try
        {
            var legacyDirectory = Directory.CreateDirectory(Path.Combine(
                root.FullName,
                "JaxI18n",
                "translation-memory")).FullName;
            var currentDirectory = Path.Combine(
                root.FullName,
                "LocaleSmith",
                "translation-memory");
            var (key, legacySnapshot) = CreateSnapshot("旧缓存");
            var (_, currentSnapshot) = CreateSnapshot("新缓存");
            var legacyPath = GetPath(legacyDirectory, key.LegacyPackageIdentity, key.TargetLanguage);
            await File.WriteAllTextAsync(
                legacyPath,
                JsonSerializer.Serialize(legacySnapshot, WebJsonOptions),
                cancellationToken);

            using var store = new FileTranslationMemoryStore(
                currentDirectory,
                [legacyDirectory],
                async (_, destinationPath, token) =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    await File.WriteAllTextAsync(
                        destinationPath,
                        JsonSerializer.Serialize(currentSnapshot, WebJsonOptions),
                        token);
                    return false;
                });
            var loaded = await store.LoadAsync(key, cancellationToken);

            Assert.Equal("新缓存", Assert.Single(Assert.Single(loaded.Entries).Value.Variants).Text);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoadTreatsMismatchedCurrentSnapshotKeyAsCacheMiss()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("localesmith-memory-wrong-current-key-");
        try
        {
            var (requestedKey, _) = CreateSnapshot("requested");
            var wrongKey = new TranslationMemoryKey(
                "different-package",
                requestedKey.TargetLanguage,
                requestedKey.ModelSourceId,
                requestedKey.TranslationContractVersion).Normalize();
            var wrongSnapshot = CreateSnapshotForKey(wrongKey, "must-not-load");
            var requestedPath = GetPath(
                directory.FullName,
                requestedKey.PackageIdentity,
                requestedKey.TargetLanguage);
            await File.WriteAllTextAsync(
                requestedPath,
                JsonSerializer.Serialize(wrongSnapshot, WebJsonOptions),
                cancellationToken);
            using var store = new FileTranslationMemoryStore(directory.FullName);

            TranslationMemorySnapshot loaded = await store.LoadAsync(requestedKey, cancellationToken);

            Assert.Equal(requestedKey, loaded.Key);
            Assert.Empty(loaded.Entries);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoadTreatsNullEntriesAsCorruptCacheMiss()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("localesmith-memory-null-entries-");
        try
        {
            var (key, _) = CreateSnapshot("requested");
            var path = GetPath(directory.FullName, key.PackageIdentity, key.TargetLanguage);
            var json = JsonSerializer.Serialize(
                new { key, entries = (object?)null },
                WebJsonOptions);
            await File.WriteAllTextAsync(path, json, cancellationToken);
            using var store = new FileTranslationMemoryStore(directory.FullName);

            TranslationMemorySnapshot loaded = await store.LoadAsync(key, cancellationToken);

            Assert.Equal(key, loaded.Key);
            Assert.Empty(loaded.Entries);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoadTreatsNullNestedVariantsAsCorruptCacheMiss()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("localesmith-memory-null-variants-");
        try
        {
            var (key, _) = CreateSnapshot("requested");
            var path = GetPath(directory.FullName, key.PackageIdentity, key.TargetLanguage);
            var json = JsonSerializer.Serialize(
                new
                {
                    key,
                    entries = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["assets/example/lang/en_us.json\0example.key"] = new
                        {
                            relativePath = "assets/example/lang/en_us.json",
                            key = "example.key",
                            sourceHash = "source-hash",
                            variants = (object?)null
                        }
                    }
                },
                WebJsonOptions);
            await File.WriteAllTextAsync(path, json, cancellationToken);
            using var store = new FileTranslationMemoryStore(directory.FullName);

            TranslationMemorySnapshot loaded = await store.LoadAsync(key, cancellationToken);

            Assert.Equal(key, loaded.Key);
            Assert.Empty(loaded.Entries);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoadTreatsOversizedSnapshotAsCacheMissWithoutReadingIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("localesmith-memory-oversized-");
        try
        {
            var (key, _) = CreateSnapshot("requested");
            var path = GetPath(directory.FullName, key.PackageIdentity, key.TargetLanguage);
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength((64L * 1024 * 1024) + 1);
            }

            using var store = new FileTranslationMemoryStore(directory.FullName);

            TranslationMemorySnapshot loaded = await store.LoadAsync(key, cancellationToken);

            Assert.Equal(key, loaded.Key);
            Assert.Empty(loaded.Entries);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SaveRejectsOversizedSnapshotWithoutPublishingPartialSlot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("localesmith-memory-save-oversized-");
        try
        {
            var (key, snapshot) = CreateSnapshot(new string('x', 1024));
            using var store = new FileTranslationMemoryStore(directory.FullName, maximumSnapshotBytes: 512);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => store.SaveAsync(snapshot, cancellationToken));

            Assert.Contains("512-byte limit", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(GetPath(directory.FullName, key.PackageIdentity, key.TargetLanguage)));
            Assert.Empty(Directory.EnumerateFiles(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoadTreatsEntryStoredUnderAnotherStableIdAsCorruptCacheMiss()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("localesmith-memory-wrong-entry-slot-");
        try
        {
            var (key, snapshot) = CreateSnapshot("must-not-load");
            var entry = Assert.Single(snapshot.Entries).Value;
            var mismatched = new TranslationMemorySnapshot(
                key,
                new Dictionary<string, TranslatedEntry>(StringComparer.Ordinal)
                {
                    ["assets/other/lang/en_us.json\0other.key"] = entry
                });
            var path = GetPath(directory.FullName, key.PackageIdentity, key.TargetLanguage);
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(mismatched, WebJsonOptions),
                cancellationToken);
            using var store = new FileTranslationMemoryStore(directory.FullName);

            TranslationMemorySnapshot loaded = await store.LoadAsync(key, cancellationToken);

            Assert.Equal(key, loaded.Key);
            Assert.Empty(loaded.Entries);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoadDoesNotPromoteMismatchedSnapshotFromLegacyRoot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("localesmith-memory-wrong-legacy-key-");
        try
        {
            var legacyDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "legacy")).FullName;
            var currentDirectory = Path.Combine(root.FullName, "current");
            var (requestedKey, _) = CreateSnapshot("requested");
            var wrongKey = new TranslationMemoryKey(
                requestedKey.RawPackageIdentity,
                "ja_jp",
                requestedKey.ModelSourceId,
                requestedKey.TranslationContractVersion).Normalize();
            var wrongSnapshot = CreateSnapshotForKey(wrongKey, "must-not-promote");
            var legacyPath = GetPath(
                legacyDirectory,
                requestedKey.LegacyPackageIdentity,
                requestedKey.TargetLanguage);
            var currentPath = GetPath(
                currentDirectory,
                requestedKey.PackageIdentity,
                requestedKey.TargetLanguage);
            await File.WriteAllTextAsync(
                legacyPath,
                JsonSerializer.Serialize(wrongSnapshot, WebJsonOptions),
                cancellationToken);
            using var store = new FileTranslationMemoryStore(
                currentDirectory,
                [legacyDirectory],
                static (_, _, _) => throw new InvalidOperationException("A mismatched snapshot must not be promoted."));

            TranslationMemorySnapshot loaded = await store.LoadAsync(requestedKey, cancellationToken);

            Assert.Equal(requestedKey, loaded.Key);
            Assert.Empty(loaded.Entries);
            Assert.False(File.Exists(currentPath));
            Assert.True(File.Exists(legacyPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static (TranslationMemoryKey Key, TranslationMemorySnapshot Snapshot) CreateSnapshot(string text)
    {
        var key = new TranslationMemoryKey(
            "example-package",
            "zh_cn",
            "source-a",
            TranslationPromptContract.CurrentVersion).Normalize();
        var entry = new TranslatedEntry(
            "assets/example/lang/en_us.json",
            "example.key",
            "source-hash",
            [new TranslationVariant(TranslationStyle.Formal, text)]);
        return (
            key,
            CreateSnapshotForKey(key, entry));
    }

    private static TranslationMemorySnapshot CreateSnapshotForKey(
        TranslationMemoryKey key,
        string text)
    {
        var entry = new TranslatedEntry(
            "assets/example/lang/en_us.json",
            "example.key",
            "source-hash",
            [new TranslationVariant(TranslationStyle.Formal, text)]);
        return CreateSnapshotForKey(key, entry);
    }

    private static TranslationMemorySnapshot CreateSnapshotForKey(
        TranslationMemoryKey key,
        TranslatedEntry entry) =>
        new(
            key,
            new Dictionary<string, TranslatedEntry>(StringComparer.Ordinal)
            {
                [GetStableId(entry)] = entry
            });

    private static string GetStableId(TranslatedEntry entry) =>
        $"{entry.RelativePath}\0{entry.Key ?? string.Empty}";

    private static string GetPath(string directory, string packageIdentity, string targetLanguage)
    {
        var identity = $"{packageIdentity}\0{targetLanguage}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(directory, $"{hash}.json");
    }
}
