using System.Text.RegularExpressions;
using System.Xml.Linq;
using LocaleSmith.Core.Services;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Tests;

public sealed class LocalizationResourceContractTests
{
    private static readonly Regex CompositeFormatPlaceholder = new(
        @"(?<!\{)\{(?<index>\d+)(?:,[^{}]+)?(?::[^{}]+)?\}(?!\})",
        RegexOptions.CultureInvariant);

    [Fact]
    public void EverySupportedApplicationLanguageHasTheSameCompleteResourceContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stringsRoot = Path.Combine(repositoryRoot, "src", "LocaleSmith.App", "Strings");
        var baseline = ReadResourcePack(stringsRoot, AppDisplayLanguages.DefaultLanguage);

        foreach (var option in AppDisplayLanguages.Catalog)
        {
            Assert.Contains(option.ResourceKey, baseline.Keys);
        }

        foreach (var language in TranslationLanguageCatalog.SupportedLanguages)
        {
            Assert.Contains($"TargetLanguage_{language.CanonicalLocale}", baseline.Keys);
        }

        foreach (var language in AppDisplayLanguages.Supported)
        {
            AssertResourceContractMatches(baseline, ReadResourcePack(stringsRoot, language), language);
        }
    }

    [Fact]
    public void EverySupportedPackageLanguageHasTheSameCompleteResourceContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stringsRoot = Path.Combine(repositoryRoot, "packaging", "LocaleSmith.Package", "Strings");
        var baseline = ReadResourcePack(stringsRoot, AppDisplayLanguages.DefaultLanguage);

        foreach (var language in AppDisplayLanguages.Supported)
        {
            AssertResourceContractMatches(baseline, ReadResourcePack(stringsRoot, language), language);
        }
    }

    [Fact]
    public void SupportedPackageLanguagesAreDeclaredInTheBuildAndManifest()
    {
        var repositoryRoot = FindRepositoryRoot();
        var packageRoot = Path.Combine(repositoryRoot, "packaging", "LocaleSmith.Package");
        var expectedLanguages = AppDisplayLanguages.Supported
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var project = XDocument.Load(Path.Combine(packageRoot, "LocaleSmith.Package.wapproj"));
        var projectNamespace = project.Root!.Name.Namespace;
        var projectLanguages = project
            .Descendants(projectNamespace + "PRIResource")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null && include.EndsWith("Resources.resw", StringComparison.Ordinal))
            .Select(include => include!.Split('\\', '/')[1])
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(expectedLanguages, projectLanguages, StringComparer.OrdinalIgnoreCase);

        var manifest = XDocument.Load(Path.Combine(packageRoot, "Package.appxmanifest"));
        var manifestNamespace = manifest.Root!.Name.Namespace;
        var manifestLanguages = manifest
            .Descendants(manifestNamespace + "Resource")
            .Select(element => (string?)element.Attribute("Language"))
            .Where(static language => !string.IsNullOrWhiteSpace(language))
            .Select(static language => language!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(expectedLanguages, manifestLanguages, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadResourcePack(
        string stringsRoot,
        string language)
    {
        var path = Path.Combine(stringsRoot, language, "Resources.resw");
        Assert.True(File.Exists(path), $"Missing {language} resource pack: {path}");

        var entries = XDocument.Load(path)
            .Root!
            .Elements("data")
            .Select(element => new
            {
                Name = (string?)element.Attribute("name"),
                Value = (string?)element.Element("value")
            })
            .ToArray();
        Assert.DoesNotContain(entries, entry => string.IsNullOrWhiteSpace(entry.Name));
        Assert.DoesNotContain(entries, entry => string.IsNullOrWhiteSpace(entry.Value));
        Assert.Equal(entries.Length, entries.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count());

        return entries.ToDictionary(
            entry => entry.Name!,
            entry => entry.Value!,
            StringComparer.Ordinal);
    }

    private static void AssertResourceContractMatches(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> candidate,
        string language)
    {
        Assert.Equal(
            baseline.Keys.Order(StringComparer.Ordinal),
            candidate.Keys.Order(StringComparer.Ordinal));
        foreach (var key in baseline.Keys)
        {
            Assert.Equal(
                ExtractPlaceholders(baseline[key]),
                ExtractPlaceholders(candidate[key]));
        }
    }

    private static string[] ExtractPlaceholders(string value) => CompositeFormatPlaceholder
        .Matches(value)
        .Select(match => match.Groups["index"].Value)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocaleSmith.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the LocaleSmith repository root.");
    }
}
