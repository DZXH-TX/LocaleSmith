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
    public async Task LoadCopiesLegacySlotToLocaleSmithSlotWithoutDeletingLegacyFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("localesmith-memory-migration-");
        try
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
                [new TranslationVariant(TranslationStyle.Formal, "译文")]);
            var snapshot = new TranslationMemorySnapshot(
                key,
                new Dictionary<string, TranslatedEntry>(StringComparer.Ordinal)
                {
                    ["entry"] = entry
                });
            var legacyPath = GetPath(directory.FullName, key.LegacyPackageIdentity, key.TargetLanguage);
            var currentPath = GetPath(directory.FullName, key.PackageIdentity, key.TargetLanguage);
            await File.WriteAllTextAsync(
                legacyPath,
                JsonSerializer.Serialize(snapshot, WebJsonOptions),
                cancellationToken);

            using var store = new FileTranslationMemoryStore(directory.FullName);
            var loaded = await store.LoadAsync(key, cancellationToken);

            Assert.Equal("译文", Assert.Single(loaded.Entries["entry"].Variants).Text);
            Assert.True(File.Exists(legacyPath));
            Assert.True(File.Exists(currentPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string GetPath(string directory, string packageIdentity, string targetLanguage)
    {
        var identity = $"{packageIdentity}\0{targetLanguage}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(directory, $"{hash}.json");
    }
}
