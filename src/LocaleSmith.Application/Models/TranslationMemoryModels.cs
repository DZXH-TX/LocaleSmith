using System.Text;
using System.Text.Json.Serialization;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Application.Models;

public static class TranslationPromptContract
{
    /// <summary>
    /// Identifies the system prompt, request envelope, response schema, and validation rules used
    /// by <see cref="Services.ModelTranslationEngine"/>. Increment this value whenever any of
    /// those semantics change in a way that can affect a cached translation.
    /// </summary>
    public const string CurrentVersion = "minecraft-java-localization-json/v2-single-style";
}

public sealed record TranslationMemoryKey
{
    private const string StorageKeySchema = "localesmith.translation-memory/v2";
    private const string LegacyStorageKeySchema = "jax-i18n.translation-memory/v2";

    public TranslationMemoryKey(
        string rawPackageIdentity,
        string targetLanguage,
        string? modelSourceId = null,
        string translationContractVersion = TranslationPromptContract.CurrentVersion)
    {
        RawPackageIdentity = rawPackageIdentity;
        TargetLanguage = targetLanguage;
        ModelSourceId = modelSourceId;
        TranslationContractVersion = translationContractVersion;
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
        LegacyStorageKeySchema);

    public string TargetLanguage { get; }

    public string? ModelSourceId { get; }

    public string TranslationContractVersion { get; }

    public TranslationMemoryKey Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RawPackageIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(TranslationContractVersion);
        return new TranslationMemoryKey(
            RawPackageIdentity.Trim(),
            TargetLanguage.Trim(),
            string.IsNullOrWhiteSpace(ModelSourceId) ? null : ModelSourceId.Trim(),
            TranslationContractVersion.Trim());
    }

    private static string CreateStoragePackageIdentity(
        string rawPackageIdentity,
        string? modelSourceId,
        string translationContractVersion,
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
            $"\0source:{sourceComponent}\0contract:{Encode(translationContractVersion.Trim())}";
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
