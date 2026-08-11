namespace LocaleSmith.Core.Models;

/// <summary>
/// Describes one target language supported by the translation pipeline.
/// </summary>
public sealed record TranslationLanguage(
    string CanonicalLocale,
    string MinecraftLocale,
    string EnglishName,
    string NativeName,
    string PromptLanguageName,
    string FormalPromptGuidance,
    string InformalPromptGuidance);
