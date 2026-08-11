using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Services;

internal static class AppLanguageBootstrapper
{
    public static string Initialize(
        string? persistedLanguage,
        Action<string> applyLanguage,
        Action initializeApplicationResources)
    {
        ArgumentNullException.ThrowIfNull(applyLanguage);
        ArgumentNullException.ThrowIfNull(initializeApplicationResources);

        var language = AppDisplayLanguages.ResolveOrDefault(persistedLanguage);
        applyLanguage(language);
        initializeApplicationResources();
        return language;
    }
}
