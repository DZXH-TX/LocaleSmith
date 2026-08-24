using System.Xml.Linq;

namespace LocaleSmith.App.Tests;

public sealed class XamlThemeContractTests
{
    private static readonly string[] XamlFolders = ["Pages", "Dialogs"];
    private static readonly XNamespace PresentationNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace MotionNamespace = "using:LocaleSmith.App.Behaviors";

    [Fact]
    public void AssistantPageExposesProjectContextPublicProcessAndProviderUsage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var page = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "LocaleSmith.App",
            "Pages",
            "AssistantPage.xaml"));
        var presentationNamespace = page.Root!.Name.Namespace;
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement projectSelector = Assert.Single(
            page.Descendants(presentationNamespace + "ComboBox"),
            element => (string?)element.Attribute(xamlNamespace + "Uid") == "AssistantProjectSelector");
        Assert.Contains(
            "Projects",
            (string?)projectSelector.Attribute("ItemsSource") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedProjectSelectionId",
            (string?)projectSelector.Attribute("SelectedValue") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal("SelectionId", (string?)projectSelector.Attribute("SelectedValuePath"));
        Assert.Contains(
            page.Descendants(presentationNamespace + "ItemsControl"),
            element => ((string?)element.Attribute("ItemsSource"))?.Contains(
                "Activities",
                StringComparison.Ordinal) is true);
        Assert.Contains(
            page.Descendants(presentationNamespace + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains(
                "UsageSummary",
                StringComparison.Ordinal) is true);
        Assert.Contains(
            page.Descendants(presentationNamespace + "TextBlock"),
            element => (string?)element.Attribute(xamlNamespace + "Uid") == "AssistantProcessLabel");
        Assert.DoesNotContain("ReasoningContent", page.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning_content", page.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExpanderMotionDoesNotDependOnAnInternalWinUiStyleKey()
    {
        var repositoryRoot = FindRepositoryRoot();
        var controls = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "LocaleSmith.App",
            "Themes",
            "Controls.xaml"));

        Assert.DoesNotContain(
            controls.Descendants(PresentationNamespace + "Style"),
            style => string.Equals(
                    (string?)style.Attribute("TargetType"),
                    "Expander",
                    StringComparison.Ordinal)
                && ((string?)style.Attribute("BasedOn"))?.Contains(
                    "DefaultExpanderStyle",
                    StringComparison.Ordinal) is true);

        var appRoot = Path.Combine(repositoryRoot, "src", "LocaleSmith.App");
        var expanders = XamlFolders
            .SelectMany(folder => Directory.EnumerateFiles(
                Path.Combine(appRoot, folder),
                "*.xaml",
                SearchOption.AllDirectories))
            .SelectMany(path => XDocument.Load(path)
                .Descendants(PresentationNamespace + "Expander"))
            .ToArray();

        Assert.NotEmpty(expanders);
        Assert.All(
            expanders,
            expander => Assert.Equal(
                "True",
                (string?)expander.Attribute(MotionNamespace + "AppMotion.ExpandFeedback")));
    }

    [Theory]
    [InlineData("ModelSourcesPage.xaml", "ModelSourcePreset", "SelectedPresetId")]
    [InlineData("OnboardingPage.xaml", "OnboardingNetworkPresetSelector", "SelectedNetworkPresetId")]
    public void ProviderPresetSelectorsUseStableTwoWayIdBindings(
        string pageName,
        string uid,
        string propertyName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var page = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "LocaleSmith.App",
            "Pages",
            pageName));
        var selector = Assert.Single(
            page.Descendants(PresentationNamespace + "ComboBox"),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Uid" &&
                string.Equals(attribute.Value, uid, StringComparison.Ordinal)));

        Assert.Equal("Id", (string?)selector.Attribute("SelectedValuePath"));
        Assert.Equal(
            $"{{Binding {propertyName}, Mode=TwoWay}}",
            (string?)selector.Attribute("SelectedValue"));
        Assert.Null(selector.Attribute("SelectionChanged"));
        Assert.Null(selector.Attribute("SelectedItem"));
    }

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
