using System.Text.Json;
using JaxI18n.Application.Models;

namespace JaxI18n.Application.Tests;

public sealed class TranslationMemoryKeyTests
{
    [Fact]
    public void StorageIdentitySeparatesModelSources()
    {
        var first = Create("cloud-source-a").Normalize();
        var second = Create("cloud-source-b").Normalize();

        Assert.Equal("example-package", first.RawPackageIdentity);
        Assert.NotEqual(first, second);
        Assert.NotEqual(GetExistingStoreSlot(first), GetExistingStoreSlot(second));
    }

    [Fact]
    public void StorageIdentitySeparatesTranslationContractVersions()
    {
        var first = Create("cloud-source", "prompt-schema/v1").Normalize();
        var second = Create("cloud-source", "prompt-schema/v2").Normalize();

        Assert.NotEqual(first, second);
        Assert.NotEqual(GetExistingStoreSlot(first), GetExistingStoreSlot(second));
    }

    [Fact]
    public void NullAndWhitespaceModelSourcesNormalizeDeterministically()
    {
        var nullSource = Create(null).Normalize();
        var whitespaceSource = Create("   ").Normalize();

        Assert.Null(nullSource.ModelSourceId);
        Assert.Equal(nullSource, whitespaceSource);
        Assert.Equal(GetExistingStoreSlot(nullSource), GetExistingStoreSlot(whitespaceSource));
    }

    [Fact]
    public void NormalizedKeyRoundTripsThroughPersistentSnapshotJson()
    {
        var key = Create("cloud-source").Normalize();

        var json = JsonSerializer.Serialize(key);
        var restored = JsonSerializer.Deserialize<TranslationMemoryKey>(json);

        Assert.Equal(key, restored);
        Assert.Equal(GetExistingStoreSlot(key), GetExistingStoreSlot(restored!));
    }

    private static TranslationMemoryKey Create(
        string? modelSourceId,
        string contractVersion = TranslationPromptContract.CurrentVersion) =>
        new(" example-package ", " zh_CN ", modelSourceId, contractVersion);

    // FileTranslationMemoryStore intentionally consumes only these two public properties. Keeping
    // this assertion here protects the compatibility bridge without coupling the test to App code.
    private static string GetExistingStoreSlot(TranslationMemoryKey key) =>
        $"{key.PackageIdentity}\0{key.TargetLanguage}";
}
