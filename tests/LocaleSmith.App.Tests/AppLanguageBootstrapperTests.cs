using LocaleSmith.App.Services;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Tests;

public sealed class AppLanguageBootstrapperTests
{
    [Fact]
    public void AppliesCanonicalLanguageBeforeInitializingApplicationResources()
    {
        var steps = new List<string>();

        var appliedLanguage = AppLanguageBootstrapper.Initialize(
            "EN-us",
            language => steps.Add($"language:{language}"),
            () => steps.Add("resources"));

        Assert.Equal(AppDisplayLanguages.EnglishUnitedStates, appliedLanguage);
        Assert.Equal(["language:en-US", "resources"], steps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr-FR")]
    public void FallsBackBeforeInitializingResourcesWhenPersistedLanguageIsUnsupported(
        string? persistedLanguage)
    {
        string? languageAtResourceInitialization = null;
        string? currentLanguage = null;

        var appliedLanguage = AppLanguageBootstrapper.Initialize(
            persistedLanguage,
            language => currentLanguage = language,
            () => languageAtResourceInitialization = currentLanguage);

        Assert.Equal(AppDisplayLanguages.DefaultLanguage, appliedLanguage);
        Assert.Equal(AppDisplayLanguages.DefaultLanguage, languageAtResourceInitialization);
    }
}
