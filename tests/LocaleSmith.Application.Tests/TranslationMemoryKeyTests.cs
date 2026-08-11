using System.Text.Json;
using LocaleSmith.Application.Models;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Application.Tests;

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

    [Fact]
    public void ExposesDistinctLegacyIdentityForOneTimeCacheMigration()
    {
        var key = Create("cloud-source").Normalize();

        Assert.Contains("localesmith.translation-memory/v3", key.PackageIdentity, StringComparison.Ordinal);
        Assert.Contains("jax-i18n.translation-memory/v2", key.LegacyPackageIdentity, StringComparison.Ordinal);
        Assert.NotEqual(key.PackageIdentity, key.LegacyPackageIdentity);
    }

    [Fact]
    public void StorageIdentitySeparatesMinecraftContentKinds()
    {
        var mod = Create("cloud-source", contentKind: MinecraftContentKind.Mod).Normalize();
        var resourcePack = Create(
            "cloud-source",
            contentKind: MinecraftContentKind.ResourcePack).Normalize();
        var shaderPack = Create(
            "cloud-source",
            contentKind: MinecraftContentKind.ShaderPack).Normalize();

        Assert.Equal(MinecraftContentKind.Mod, mod.ContentKind);
        Assert.NotEqual(GetExistingStoreSlot(mod), GetExistingStoreSlot(resourcePack));
        Assert.NotEqual(GetExistingStoreSlot(resourcePack), GetExistingStoreSlot(shaderPack));
        Assert.NotEqual(GetExistingStoreSlot(shaderPack), GetExistingStoreSlot(mod));
    }

    [Fact]
    public void NormalizeRejectsAnUnknownEnumValue()
    {
        var key = Create("cloud-source", contentKind: (MinecraftContentKind)999);

        Assert.Throws<ArgumentException>(() => key.Normalize());
    }

    [Fact]
    public void TargetLanguageAliasNormalizesToCanonicalCacheKey()
    {
        var canonical = new TranslationMemoryKey("example-package", "ja_JP").Normalize();
        var alias = new TranslationMemoryKey("example-package", " JA-jp ").Normalize();

        Assert.Equal("ja_JP", alias.TargetLanguage);
        Assert.Equal(canonical, alias);
        Assert.Equal(GetExistingStoreSlot(canonical), GetExistingStoreSlot(alias));
    }

    private static TranslationMemoryKey Create(
        string? modelSourceId,
        string contractVersion = TranslationPromptContract.CurrentVersion,
        MinecraftContentKind contentKind = MinecraftContentKind.Unknown) =>
        new(" example-package ", " zh_CN ", modelSourceId, contractVersion, contentKind);

    // FileTranslationMemoryStore intentionally consumes only these two public properties. Keeping
    // this assertion here protects the compatibility bridge without coupling the test to App code.
    private static string GetExistingStoreSlot(TranslationMemoryKey key) =>
        $"{key.PackageIdentity}\0{key.TargetLanguage}";
}
