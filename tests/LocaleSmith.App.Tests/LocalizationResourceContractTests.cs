using System.Text.RegularExpressions;
using System.Xml.Linq;
using LocaleSmith.App.Pages;
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
    public void CommunityPageExposesTermsAndGuidelinesInEverySupportedLanguage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var page = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "LocaleSmith.App",
            "Pages",
            "CommunityPage.xaml"));
        var presentationNamespace = page.Root!.Name.Namespace;
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        var links = page
            .Descendants(presentationNamespace + "HyperlinkButton")
            .Where(element => element.Attribute(xamlNamespace + "Uid") is not null)
            .Select(element => new
            {
                Uid = (string)element.Attribute(xamlNamespace + "Uid")!,
                NavigateUri = (string?)element.Attribute("NavigateUri")
            })
            .ToArray();

        Assert.Contains(
            links,
            link => link.Uid == "CommunityTermsLink" && link.NavigateUri == "{Binding TermsUri}");
        Assert.Contains(
            links,
            link => link.Uid == "CommunityGuidelinesLink" &&
                link.NavigateUri == "{Binding CommunityGuidelinesUri}");

        var stringsRoot = Path.Combine(repositoryRoot, "src", "LocaleSmith.App", "Strings");
        foreach (var language in AppDisplayLanguages.Supported)
        {
            var resources = ReadResourcePack(stringsRoot, language);
            Assert.Contains("CommunityTermsLink.Content", resources.Keys);
            Assert.Contains("CommunityGuidelinesLink.Content", resources.Keys);
        }
    }

    [Fact]
    public void CommunityPageRequiresVerifiedSignInBeforeShowingCommunityContent()
    {
        var repositoryRoot = FindRepositoryRoot();
        var page = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "LocaleSmith.App",
            "Pages",
            "CommunityPage.xaml"));
        var presentationNamespace = page.Root!.Name.Namespace;
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        var gate = Assert.Single(
            page.Descendants(presentationNamespace + "Border"),
            element => (string?)element.Attribute(xamlNamespace + "Name") == "CommunitySignInGate");
        Assert.True(HasVisibilityBinding(gate, "ShowSignInForm"));

        var authenticatedContent = Assert.Single(
            page.Descendants(presentationNamespace + "Grid"),
            element => (string?)element.Attribute(xamlNamespace + "Name") ==
                "AuthenticatedCommunityContent");
        Assert.True(HasVisibilityBinding(authenticatedContent, "IsAuthenticated"));

        Assert.Single(
            gate.Descendants(presentationNamespace + "TextBox"),
            element => (string?)element.Attribute(xamlNamespace + "Name") ==
                "CommunityUsernameInput");
        Assert.Single(
            gate.Descendants(presentationNamespace + "PasswordBox"),
            element => (string?)element.Attribute(xamlNamespace + "Name") ==
                "CommunityPasswordInput");
        Assert.Single(
            gate.Descendants(presentationNamespace + "PasswordBox"),
            element => (string?)element.Attribute(xamlNamespace + "Name") ==
                "CommunityPatInput");
        Assert.Contains(
            gate.Descendants(presentationNamespace + "Button"),
            element => HasUidAndClick(
                element,
                xamlNamespace,
                "CommunityApplicationLoginButton",
                "OnApplicationLoginClicked"));
        Assert.Contains(
            gate.Descendants(presentationNamespace + "HyperlinkButton"),
            element => (string?)element.Attribute(xamlNamespace + "Uid") ==
                    "CommunityRegisterAccountLink"
                && (string?)element.Attribute("NavigateUri") ==
                    "https://dow.dzxh-tx.cn/register?next=/user/dashboard");

        var stringsRoot = Path.Combine(repositoryRoot, "src", "LocaleSmith.App", "Strings");
        foreach (var language in AppDisplayLanguages.Supported)
        {
            var resources = ReadResourcePack(stringsRoot, language);
            Assert.Contains("CommunitySignInGateTitle.Text", resources.Keys);
            Assert.Contains("CommunitySignInGateBody.Text", resources.Keys);
            Assert.Contains("CommunityUsernameInput.Header", resources.Keys);
            Assert.Contains("CommunityPasswordInput.Header", resources.Keys);
            Assert.Contains("CommunityApplicationTokenInput.Header", resources.Keys);
            Assert.Contains("CommunityRegisterAccountLink.Content", resources.Keys);
        }
    }

    [Fact]
    public void CommunityPageExposesNativeReportFlowForEveryVisibleUgcTarget()
    {
        var repositoryRoot = FindRepositoryRoot();
        var page = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "LocaleSmith.App",
            "Pages",
            "CommunityPage.xaml"));
        var presentationNamespace = page.Root!.Name.Namespace;
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        var buttons = page.Descendants(presentationNamespace + "Button").ToArray();

        Assert.Contains(buttons, element => HasUidAndClick(
            element,
            xamlNamespace,
            "CommunityReportModButton",
            "OnReportModClicked"));
        Assert.Contains(buttons, element => HasUidAndClick(
            element,
            xamlNamespace,
            "CommunityReportVersionButton",
            "OnReportVersionClicked"));
        Assert.Contains(buttons, element => HasUidAndClick(
            element,
            xamlNamespace,
            "CommunityReportThreadButton",
            "OnReportThreadClicked"));
        Assert.Contains(buttons, element => HasUidAndClick(
            element,
            xamlNamespace,
            "CommunityReportPostButton",
            "OnReportPostClicked"));
        Assert.Contains(buttons, element => HasUidAndClick(
            element,
            xamlNamespace,
            "CommunityReportUserButton",
            "OnReportModOwnerClicked"));
        Assert.Contains(buttons, element => HasUidAndClick(
            element,
            xamlNamespace,
            "CommunityReportUserButton",
            "OnReportThreadAuthorClicked"));
        Assert.Contains(buttons, element => HasUidAndClick(
            element,
            xamlNamespace,
            "CommunityReportUserButton",
            "OnReportPostAuthorClicked"));
        Assert.DoesNotContain(
            buttons,
            element => ((string?)element.Attribute(xamlNamespace + "Uid"))?.StartsWith(
                "CommunityReport",
                StringComparison.Ordinal) is true
                && element.Attribute("IsEnabled") is not null);

        var reportDialog = Assert.Single(
            page.Descendants(presentationNamespace + "ContentDialog"),
            element => (string?)element.Attribute(xamlNamespace + "Name") == "ReportContentDialog");
        Assert.Equal(
            "OnReportPrimaryButtonClick",
            (string?)reportDialog.Attribute("PrimaryButtonClick"));
        Assert.Equal(
            "OnReportDialogClosing",
            (string?)reportDialog.Attribute("Closing"));
        Assert.NotEmpty(reportDialog.Descendants(presentationNamespace + "ProgressRing"));
        Assert.NotEmpty(reportDialog.Descendants(presentationNamespace + "InfoBar"));
        var details = Assert.Single(
            reportDialog.Descendants(presentationNamespace + "TextBox"),
            element => (string?)element.Attribute(xamlNamespace + "Name") == "ReportDetailsInput");
        Assert.Equal("1900", (string?)details.Attribute("MaxLength"));

        var accessDialog = Assert.Single(
            page.Descendants(presentationNamespace + "ContentDialog"),
            element => (string?)element.Attribute(xamlNamespace + "Name") == "ReportAccessDialog");
        Assert.Contains(
            accessDialog.Descendants(presentationNamespace + "HyperlinkButton"),
            element => (string?)element.Attribute(xamlNamespace + "Uid") == "CommunityCreatePatLink");
        Assert.Contains(
            accessDialog.Descendants(presentationNamespace + "HyperlinkButton"),
            element => (string?)element.Attribute(xamlNamespace + "Uid") == "CommunityGuidelinesLink");

        var stringsRoot = Path.Combine(repositoryRoot, "src", "LocaleSmith.App", "Strings");
        foreach (var language in AppDisplayLanguages.Supported)
        {
            var resources = ReadResourcePack(stringsRoot, language);
            Assert.Contains("CommunityReportDialog.Title", resources.Keys);
            Assert.Contains("CommunityReportCategoryChildSafety", resources.Keys);
            Assert.Contains("CommunityReportAccessMessage.Text", resources.Keys);
            Assert.Contains("CommunityReportSecurityUnavailableError", resources.Keys);
        }
    }

    [Fact]
    public void CommunityPageKeepsSelectedContentReportActionsReachableInCompactLayout()
    {
        var repositoryRoot = FindRepositoryRoot();
        var page = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "LocaleSmith.App",
            "Pages",
            "CommunityPage.xaml"));
        var presentationNamespace = page.Root!.Name.Namespace;
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        var compactSelectors = Assert.Single(
            page.Descendants(presentationNamespace + "Grid"),
            element => string.Equals(
                (string?)element.Attribute(xamlNamespace + "Name"),
                "CompactSelectors",
                StringComparison.Ordinal));
        var buttons = compactSelectors.Descendants(presentationNamespace + "Button").ToArray();

        Assert.Contains(buttons, element => HasUidAndClick(
            element,
            xamlNamespace,
            "CommunityReportModButton",
            "OnReportSelectedModClicked"));
        var versionButton = Assert.Single(buttons, element => HasUidAndClick(
            element,
            xamlNamespace,
            "CommunityReportVersionButton",
            "OnReportSelectedModVersionClicked"));
        Assert.True(HasVisibilityBinding(versionButton, "SelectedMod.HasLatestVersion"));
        Assert.Contains(buttons, element => HasUidAndClick(
            element,
            xamlNamespace,
            "CommunityReportUserButton",
            "OnReportSelectedModOwnerClicked"));
        Assert.Contains(buttons, element => HasUidAndClick(
            element,
            xamlNamespace,
            "CommunityReportThreadButton",
            "OnReportSelectedThreadClicked"));
        Assert.Contains(buttons, element => HasUidAndClick(
            element,
            xamlNamespace,
            "CommunityReportUserButton",
            "OnReportSelectedThreadAuthorClicked"));

        var modActions = Assert.Single(
            compactSelectors.Descendants(presentationNamespace + "Grid"),
            element => string.Equals(
                (string?)element.Attribute(xamlNamespace + "Name"),
                "CompactModReportActions",
                StringComparison.Ordinal));
        var threadActions = Assert.Single(
            compactSelectors.Descendants(presentationNamespace + "Grid"),
            element => string.Equals(
                (string?)element.Attribute(xamlNamespace + "Name"),
                "CompactThreadReportActions",
                StringComparison.Ordinal));
        Assert.True(HasVisibilityBinding(modActions, "HasSelectedMod"));
        Assert.True(HasVisibilityBinding(threadActions, "HasSelectedThread"));

        var compactState = Assert.Single(
            page.Descendants(presentationNamespace + "VisualState"),
            element => string.Equals(
                (string?)element.Attribute(xamlNamespace + "Name"),
                "CompactCommunityLayout",
                StringComparison.Ordinal));
        Assert.Contains(
            compactState.Descendants(presentationNamespace + "Setter"),
            element => string.Equals(
                    (string?)element.Attribute("Target"),
                    "CompactSelectors.Visibility",
                    StringComparison.Ordinal)
                && string.Equals(
                    (string?)element.Attribute("Value"),
                    "Visible",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void CommunityReportDialogClosingPolicyBlocksOnlyActiveUserDismissal()
    {
        Assert.False(CommunityPage.ShouldBlockReportDialogClosing(
            isReportSubmitting: false,
            allowForcedClose: false));
        Assert.True(CommunityPage.ShouldBlockReportDialogClosing(
            isReportSubmitting: true,
            allowForcedClose: false));
        Assert.False(CommunityPage.ShouldBlockReportDialogClosing(
            isReportSubmitting: true,
            allowForcedClose: true));
    }

    [Fact]
    public void CommunityPageMatchesSearchAndCompactEmptyStateContracts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var page = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "LocaleSmith.App",
            "Pages",
            "CommunityPage.xaml"));
        var presentationNamespace = page.Root!.Name.Namespace;
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        var searchInput = Assert.Single(
            page.Descendants(presentationNamespace + "TextBox"),
            element => string.Equals(
                (string?)element.Attribute(xamlNamespace + "Name"),
                "CommunitySearchInput",
                StringComparison.Ordinal));
        Assert.Equal("100", (string?)searchInput.Attribute("MaxLength"));

        var postsPane = Assert.Single(
            page.Descendants(presentationNamespace + "Border"),
            element => string.Equals(
                (string?)element.Attribute(xamlNamespace + "Name"),
                "PostsPane",
                StringComparison.Ordinal));
        Assert.Contains(
            postsPane.Descendants(presentationNamespace + "StackPanel"),
            element => HasVisibilityBinding(element, "ShowModsEmptyState") &&
                HasLocalizedDescendant(element, xamlNamespace, "CommunityModsEmptyTitle"));
        Assert.Contains(
            postsPane.Descendants(presentationNamespace + "StackPanel"),
            element => HasVisibilityBinding(element, "ShowThreadsEmptyState") &&
                HasLocalizedDescendant(element, xamlNamespace, "CommunityThreadsEmptyTitle"));

        var selectThreadPrompt = Assert.Single(
            postsPane.Descendants(presentationNamespace + "TextBlock"),
            element => string.Equals(
                (string?)element.Attribute(xamlNamespace + "Uid"),
                "CommunitySelectThreadPrompt",
                StringComparison.Ordinal));
        Assert.True(HasVisibilityBinding(selectThreadPrompt.Parent!, "ShowSelectThreadPrompt"));
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

    [Fact]
    public void StorePackageManifestUsesThePartnerCenterIdentityAndVersionContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(
            repositoryRoot,
            "packaging",
            "LocaleSmith.Package",
            "Package.appxmanifest");
        var manifest = XDocument.Load(manifestPath);
        var manifestNamespace = manifest.Root!.Name.Namespace;
        var identity = Assert.Single(manifest.Descendants(manifestNamespace + "Identity"));

        Assert.Equal("CRTech.LocaleSmith", (string?)identity.Attribute("Name"));
        Assert.Equal(
            "CN=33E83C71-5BAE-4CB2-A70A-1F0545DACFB1",
            (string?)identity.Attribute("Publisher"));

        var publisherDisplayName = Assert.Single(
            manifest.Descendants(manifestNamespace + "PublisherDisplayName"));
        Assert.Equal("DZXH CR Tech", publisherDisplayName.Value);

        var packageVersion = Version.Parse(
            Assert.IsType<string>((string?)identity.Attribute("Version")));
        Assert.Equal(new Version(1, 2, 0, 0), packageVersion);
        Assert.Equal(4, AppConfiguration.CurrentSchemaVersion);
    }

    [Fact]
    public void DevelopmentPackageUsesAnIsolatedIdentityAndTheSameReleaseVersion()
    {
        var repositoryRoot = FindRepositoryRoot();
        var packageRoot = Path.Combine(repositoryRoot, "packaging", "LocaleSmith.Package");
        var production = XDocument.Load(Path.Combine(packageRoot, "Package.appxmanifest"));
        var development = XDocument.Load(Path.Combine(packageRoot, "Package.dev.appxmanifest"));
        var productionNamespace = production.Root!.Name.Namespace;
        var developmentNamespace = development.Root!.Name.Namespace;
        var productionIdentity = Assert.Single(
            production.Descendants(productionNamespace + "Identity"));
        var developmentIdentity = Assert.Single(
            development.Descendants(developmentNamespace + "Identity"));

        Assert.Equal("CRTech.LocaleSmith.Dev", (string?)developmentIdentity.Attribute("Name"));
        Assert.Equal("CN=LocaleSmith Development", (string?)developmentIdentity.Attribute("Publisher"));
        Assert.Equal(
            (string?)productionIdentity.Attribute("Version"),
            (string?)developmentIdentity.Attribute("Version"));
        Assert.NotEqual(
            (string?)productionIdentity.Attribute("Name"),
            (string?)developmentIdentity.Attribute("Name"));
        Assert.Equal(
            (string?)productionIdentity.Attribute("ProcessorArchitecture"),
            (string?)developmentIdentity.Attribute("ProcessorArchitecture"));
        Assert.True(XNode.DeepEquals(
            production.Root.Element(productionNamespace + "Resources"),
            development.Root.Element(developmentNamespace + "Resources")));
        Assert.True(XNode.DeepEquals(
            production.Root.Element(productionNamespace + "Dependencies"),
            development.Root.Element(developmentNamespace + "Dependencies")));
        Assert.True(XNode.DeepEquals(
            production.Root.Element(productionNamespace + "Capabilities"),
            development.Root.Element(developmentNamespace + "Capabilities")));
        var productionApplication = Assert.Single(
            production.Descendants(productionNamespace + "Application"));
        var developmentApplication = Assert.Single(
            development.Descendants(developmentNamespace + "Application"));
        Assert.Equal(
            (string?)productionApplication.Attribute("Executable"),
            (string?)developmentApplication.Attribute("Executable"));
        Assert.Equal(
            (string?)productionApplication.Attribute("EntryPoint"),
            (string?)developmentApplication.Attribute("EntryPoint"));

        var project = XDocument.Load(Path.Combine(packageRoot, "LocaleSmith.Package.wapproj"));
        var projectNamespace = project.Root!.Name.Namespace;
        var defaultFlavor = Assert.Single(
            project.Descendants(projectNamespace + "PackageFlavor"),
            element => string.Equals(
                (string?)element.Attribute("Condition"),
                "'$(PackageFlavor)' == ''",
                StringComparison.Ordinal));
        Assert.Equal("Development", defaultFlavor.Value);
        var manifests = project.Descendants(projectNamespace + "AppxManifest").ToArray();
        Assert.Contains(
            manifests,
            element => (string?)element.Attribute("Include") == "Package.dev.appxmanifest"
                && ((string?)element.Attribute("Condition"))?.Contains(
                    "'$(PackageFlavor)' == 'Development'",
                    StringComparison.Ordinal) is true);
        Assert.Contains(
            manifests,
            element => (string?)element.Attribute("Include") == "Package.appxmanifest"
                && ((string?)element.Attribute("Condition"))?.Contains(
                    "'$(PackageFlavor)' == 'Store'",
                    StringComparison.Ordinal) is true);
    }

    [Fact]
    public void FullTrustPackageDeclaresTheRequiredRestrictedCapability()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifest = XDocument.Load(Path.Combine(
            repositoryRoot,
            "packaging",
            "LocaleSmith.Package",
            "Package.appxmanifest"));
        var manifestNamespace = manifest.Root!.Name.Namespace;
        XNamespace restrictedCapabilityNamespace =
            "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

        var application = Assert.Single(
            manifest.Descendants(manifestNamespace + "Application"));
        Assert.Equal(
            "Windows.FullTrustApplication",
            (string?)application.Attribute("EntryPoint"));
        Assert.Contains(
            manifest.Descendants(restrictedCapabilityNamespace + "Capability"),
            capability => string.Equals(
                (string?)capability.Attribute("Name"),
                "runFullTrust",
                StringComparison.Ordinal));
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

    private static bool HasLocalizedDescendant(
        XElement element,
        XNamespace xamlNamespace,
        string uid) => element
        .Descendants()
        .Any(descendant => string.Equals(
            (string?)descendant.Attribute(xamlNamespace + "Uid"),
            uid,
            StringComparison.Ordinal));

    private static bool HasUidAndClick(
        XElement element,
        XNamespace xamlNamespace,
        string uid,
        string click) =>
        string.Equals(
            (string?)element.Attribute(xamlNamespace + "Uid"),
            uid,
            StringComparison.Ordinal)
        && string.Equals((string?)element.Attribute("Click"), click, StringComparison.Ordinal);

    private static bool HasVisibilityBinding(XElement element, string propertyName) =>
        ((string?)element.Attribute("Visibility"))?.Contains(
            $"Binding {propertyName}",
            StringComparison.Ordinal) is true;

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
