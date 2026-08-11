using System.Diagnostics.CodeAnalysis;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Core.Services;

/// <summary>
/// Provides the canonical target-language identifiers and model guidance used throughout the
/// application. Add future languages here so request validation, archive naming, and model prompts
/// continue to share one definition.
/// </summary>
public static class TranslationLanguageCatalog
{
    public const string DefaultLocale = "zh_CN";

    private static readonly IReadOnlyList<TranslationLanguage> Languages = Array.AsReadOnly(
    [
        new TranslationLanguage(
            "zh_CN",
            "zh_cn",
            "Chinese (Simplified)",
            "简体中文",
            "Simplified Chinese",
            "Use official Minecraft Simplified Chinese terminology and a consistent, clear written register.",
            "Use established Simplified Chinese player-community wording and natural colloquial phrasing while remaining accurate and non-offensive."),
        new TranslationLanguage(
            "en_US",
            "en_us",
            "English (United States)",
            "English (United States)",
            "English (United States)",
            "Use official Minecraft English (United States) terminology and a consistent, neutral written register.",
            "Use established English-speaking player-community wording and natural casual American English while remaining accurate and non-offensive."),
        new TranslationLanguage(
            "ja_JP",
            "ja_jp",
            "Japanese",
            "日本語",
            "Japanese",
            "Use official Minecraft Japanese terminology and a consistent standard written register.",
            "Use established Japanese player-community wording and natural casual phrasing while remaining accurate and non-offensive."),
        new TranslationLanguage(
            "fr_FR",
            "fr_fr",
            "French",
            "Français",
            "French",
            "Use official Minecraft French terminology and a consistent standard written register.",
            "Use established French player-community wording and natural colloquial phrasing while remaining accurate and non-offensive."),
        new TranslationLanguage(
            "ru_RU",
            "ru_ru",
            "Russian",
            "Русский",
            "Russian",
            "Use official Minecraft Russian terminology and a consistent standard written register.",
            "Use established Russian player-community wording and natural colloquial phrasing while remaining accurate and non-offensive.")
    ]);

    private static readonly Dictionary<string, TranslationLanguage> LanguagesByLocale =
        Languages.ToDictionary(
            static language => language.CanonicalLocale,
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets supported languages in their stable user-facing order.
    /// </summary>
    public static IReadOnlyList<TranslationLanguage> SupportedLanguages => Languages;

    public static TranslationLanguage GetRequired(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        if (TryGet(locale, out var language))
        {
            return language;
        }

        var supported = string.Join(", ", Languages.Select(static item => item.CanonicalLocale));
        throw new ArgumentException(
            $"Unsupported target language locale '{locale}'. Supported locales: {supported}.",
            nameof(locale));
    }

    public static bool TryGet(
        string? locale,
        [NotNullWhen(true)] out TranslationLanguage? language)
    {
        language = null;
        if (!TryCreateLookupKey(locale, out var key))
        {
            return false;
        }

        return LanguagesByLocale.TryGetValue(key, out language);
    }

    public static string NormalizeLocale(string locale) => GetRequired(locale).CanonicalLocale;

    public static bool TryNormalizeLocale(
        string? locale,
        [NotNullWhen(true)] out string? canonicalLocale)
    {
        if (TryGet(locale, out var language))
        {
            canonicalLocale = language.CanonicalLocale;
            return true;
        }

        canonicalLocale = null;
        return false;
    }

    /// <summary>
    /// Normalizes a safe Minecraft locale token without asserting that it is a supported target
    /// language. This is intended for inspecting arbitrary source resources such as <c>de_de</c>.
    /// </summary>
    public static string NormalizeMinecraftLocaleToken(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        if (TryNormalizeMinecraftLocaleToken(locale, out var normalizedLocale))
        {
            return normalizedLocale;
        }

        throw new ArgumentException($"Unsafe Minecraft locale identifier '{locale}'.", nameof(locale));
    }

    public static bool TryNormalizeMinecraftLocaleToken(
        string? locale,
        [NotNullWhen(true)] out string? normalizedLocale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            normalizedLocale = null;
            return false;
        }

        var candidate = locale.Trim().Replace('-', '_').ToLowerInvariant();
        if (candidate.Length == 0 || candidate.Any(static character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
        {
            normalizedLocale = null;
            return false;
        }

        normalizedLocale = candidate;
        return true;
    }

    private static bool TryCreateLookupKey(
        string? locale,
        [NotNullWhen(true)] out string? key)
    {
        if (!TryNormalizeMinecraftLocaleToken(locale, out var normalizedLocale))
        {
            key = null;
            return false;
        }

        key = normalizedLocale;
        return true;
    }
}
