using System.Xml.Linq;

namespace LocaleSmith.App.Tests;

public sealed class MicrosoftStoreBillingXamlContractTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void BillingPurchaseEntryIsFailClosedAndKeepsRestoreRefreshAndCancellationLinks()
    {
        var control = LoadControl("MicrosoftStoreBillingControl.xaml");
        var ns = control.Root!.Name.Namespace;
        var purchase = FindByUid(control, ns + "Button", "BillingPurchaseButton");
        var restore = FindByUid(control, ns + "Button", "BillingRestoreButton");
        var refresh = FindByUid(control, ns + "Button", "BillingRefreshButton");
        var manage = FindByUid(control, ns + "HyperlinkButton", "BillingManageSubscriptionsLink");
        var privacy = FindByUid(control, ns + "HyperlinkButton", "BillingPrivacyLink");

        Assert.Contains("IsPurchaseEntryVisible", (string?)purchase.Attribute("Visibility"), StringComparison.Ordinal);
        Assert.Contains("PurchaseCommand", (string?)purchase.Attribute("Command"), StringComparison.Ordinal);
        Assert.Contains("AreStoreActionsVisible", (string?)restore.Attribute("Visibility"), StringComparison.Ordinal);
        Assert.Contains("RestoreCommand", (string?)restore.Attribute("Command"), StringComparison.Ordinal);
        Assert.Contains("RefreshCommand", (string?)refresh.Attribute("Command"), StringComparison.Ordinal);
        Assert.Contains("ManageSubscriptionsUri", (string?)manage.Attribute("NavigateUri"), StringComparison.Ordinal);
        Assert.Contains("PrivacyPolicyUri", (string?)privacy.Attribute("NavigateUri"), StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicStorePriceRemainsBoundAndReadableWithItsLocalizedCaption()
    {
        var control = LoadControl("MicrosoftStoreBillingControl.xaml");
        var ns = control.Root!.Name.Namespace;
        Assert.Contains(
            control.Descendants(ns + "TextBlock"),
            element => (string?)element.Attribute(XamlNamespace + "Uid") == "BillingStorePriceCaption");
        Assert.Contains(
            control.Descendants(ns + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains(
                "FormattedStorePrice",
                StringComparison.Ordinal) is true
                && element.Attribute(XamlNamespace + "Uid") is null);
    }

    [Fact]
    public void AcceleratedEntryIsServerGatedWhileDefaultDownloadAlwaysRemainsReachable()
    {
        var control = LoadControl("ModArtifactDownloadControl.xaml");
        var ns = control.Root!.Name.Namespace;
        var defaultButton = FindByUid(control, ns + "Button", "CommunityDefaultDownloadButton");
        var acceleratedButton = FindByUid(control, ns + "Button", "CommunityAcceleratedDownloadButton");
        var error = FindByUid(control, ns + "InfoBar", "CommunityDownloadErrorInfo");
        var status = FindByUid(control, ns + "InfoBar", "CommunityDownloadStatusInfo");

        Assert.Null(defaultButton.Attribute("Visibility"));
        Assert.Contains(
            "IsAccelerationAvailable",
            (string?)acceleratedButton.Attribute("Visibility"),
            StringComparison.Ordinal);
        Assert.Equal("Assertive", (string?)error.Attribute("AutomationProperties.LiveSetting"));
        Assert.Equal("Polite", (string?)status.Attribute("AutomationProperties.LiveSetting"));
    }

    [Fact]
    public void BillingAndDownloadViewsContainNoSecretFieldsHostsOrCustomMotion()
    {
        var billing = File.ReadAllText(ControlPath("MicrosoftStoreBillingControl.xaml"));
        var download = File.ReadAllText(ControlPath("ModArtifactDownloadControl.xaml"));
        var combined = billing + download;

        foreach (var forbidden in new[]
                 {
                     "bucket.dzxh-tx.cn",
                     "cn-nb1.rains3.com",
                     "store_id_key",
                     "service_ticket",
                     "mctx_pat_",
                     "Storyboard"
                 })
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ChineseBillingDisclosureStatesTrialRenewalRegionalPricingAndMicrosoftCancellation()
    {
        var resources = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "LocaleSmith.App",
            "Strings",
            "zh-CN",
            "Resources.resw"));
        var values = resources
            .Root!
            .Elements("data")
            .Where(element => ((string?)element.Attribute("name"))?.StartsWith(
                "Billing",
                StringComparison.Ordinal) is true)
            .Select(element => element.Element("value")?.Value ?? string.Empty)
            .ToArray();
        var combined = string.Join('\n', values);

        Assert.Contains("7 天免费试用", combined, StringComparison.Ordinal);
        Assert.Contains("自动续费", combined, StringComparison.Ordinal);
        Assert.Contains("US$4.99", combined, StringComparison.Ordinal);
        Assert.Contains("CNY 30.00", combined, StringComparison.Ordinal);
        Assert.Contains("Microsoft", combined, StringComparison.Ordinal);
        Assert.Contains("取消", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("首月", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("CNY 24", combined, StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument LoadControl(string filename) => XDocument.Load(ControlPath(filename));

    private static string ControlPath(string filename) => Path.Combine(
        FindRepositoryRoot(),
        "src",
        "LocaleSmith.App",
        "Controls",
        filename);

    private static XElement FindByUid(XDocument document, XName name, string uid) =>
        Assert.Single(
            document.Descendants(name),
            element => (string?)element.Attribute(XamlNamespace + "Uid") == uid);

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
