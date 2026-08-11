using System.Text;
using System.Text.Json.Serialization;
using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;

namespace LocaleSmith.Application.Models;

public static class TranslationPromptContract
{
    /// <summary>
    /// Identifies the system prompt, request envelope, response schema, and validation rules used
    /// by <see cref="Services.ModelTranslationEngine"/>. Increment this value whenever any of
    /// those semantics change in a way that can affect a cached translation.
    /// </summary>
    public const string CurrentVersion = "minecraft-java-localization-json/v4-content-profiles-glossaries";
}

public sealed record TranslationMemoryKey
{
    private const string StorageKeySchema = "localesmith.translation-memory/v3";
    private const string LegacyStorageKeySchema = "jax-i18n.translation-memory/v2";

    public TranslationMemoryKey(
        string rawPackageIdentity,
        string targetLanguage,
        string? modelSourceId = null,
        string translationContractVersion = TranslationPromptContract.CurrentVersion,
        MinecraftContentKind contentKind = MinecraftContentKind.Unknown)
    {
        RawPackageIdentity = rawPackageIdentity;
        TargetLanguage = targetLanguage;
        ModelSourceId = modelSourceId;
        TranslationContractVersion = translationContractVersion;
        ContentKind = contentKind;
    }

    public string RawPackageIdentity { get; }

    /// <summary>
    /// Gets the versioned storage namespace consumed by existing memory stores. It deliberately
    /// incorporates the raw package identity, captured model source, and translation contract so
    /// legacy two-component cache files become safe misses instead of being reused incorrectly.
    /// </summary>
    [JsonIgnore]
    public string PackageIdentity => CreateStoragePackageIdentity(
        RawPackageIdentity,
        ModelSourceId,
        TranslationContractVersion,
        ContentKind,
        StorageKeySchema);

    /// <summary>
    /// Gets the pre-LocaleSmith identity used only to discover and migrate existing cache files.
    /// New snapshots are never written with this identity.
    /// </summary>
    [JsonIgnore]
    public string LegacyPackageIdentity => CreateStoragePackageIdentity(
        RawPackageIdentity,
        ModelSourceId,
        TranslationContractVersion,
        ContentKind,
        LegacyStorageKeySchema);

    public string TargetLanguage { get; }

    public string? ModelSourceId { get; }

    public string TranslationContractVersion { get; }

    public MinecraftContentKind ContentKind { get; }

    public TranslationMemoryKey Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RawPackageIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(TranslationContractVersion);
        if (!Enum.IsDefined(ContentKind))
        {
            throw new ArgumentException("The Minecraft content kind is not valid.", nameof(ContentKind));
        }

        return new TranslationMemoryKey(
            RawPackageIdentity.Trim(),
            TranslationLanguageCatalog.NormalizeLocale(TargetLanguage),
            string.IsNullOrWhiteSpace(ModelSourceId) ? null : ModelSourceId.Trim(),
            TranslationContractVersion.Trim(),
            ContentKind);
    }

    private static string CreateStoragePackageIdentity(
        string rawPackageIdentity,
        string? modelSourceId,
        string translationContractVersion,
        MinecraftContentKind contentKind,
        string storageKeySchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPackageIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(translationContractVersion);
        var normalizedSourceId = string.IsNullOrWhiteSpace(modelSourceId)
            ? null
            : modelSourceId.Trim();
        var sourceComponent = normalizedSourceId is null
            ? "null"
            : $"value:{Encode(normalizedSourceId)}";

        // The leading NUL also keeps this namespace disjoint from legacy package identities,
        // which originate in archive metadata or file names and cannot contain NUL characters.
        return $"\0{storageKeySchema}\0package:{Encode(rawPackageIdentity.Trim())}" +
            $"\0source:{sourceComponent}\0contract:{Encode(translationContractVersion.Trim())}" +
            $"\0content:{contentKind}";
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}

public sealed record TranslationMemorySnapshot(
    TranslationMemoryKey Key,
    IReadOnlyDictionary<string, TranslatedEntry> Entries)
{
    public static TranslationMemorySnapshot Empty(TranslationMemoryKey key) =>
        new(key.Normalize(), new Dictionary<string, TranslatedEntry>(StringComparer.Ordinal));
}
