using System.Xml.Linq;

namespace LocaleSmith.App.Tests;

public sealed class XamlThemeContractTests
{
    private static readonly string[] XamlFolders = ["Pages", "Dialogs"];
    private static readonly XNamespace PresentationNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace MotionNamespace = "using:LocaleSmith.App.Behaviors";

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
