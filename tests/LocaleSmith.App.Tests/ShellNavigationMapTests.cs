using LocaleSmith.App.Pages;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Tests;

public sealed class ShellNavigationMapTests
{
    public static TheoryData<ShellSection, Type, bool, string, string?> Targets => new()
    {
        { ShellSection.Onboarding, typeof(OnboardingPage), false, "None", null },
        { ShellSection.Dashboard, typeof(DashboardPage), true, "MenuItem", "Dashboard" },
        { ShellSection.Assistant, typeof(AssistantPage), true, "MenuItem", "Assistant" },
        { ShellSection.Community, typeof(CommunityPage), true, "MenuItem", "Community" },
        { ShellSection.ModelSources, typeof(ModelSourcesPage), true, "MenuItem", "ModelSources" },
        { ShellSection.Logs, typeof(LogsPage), true, "MenuItem", "Logs" },
        { ShellSection.Settings, typeof(SettingsPage), true, "Settings", null }
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public void EverySectionMapsToItsPageAndSelection(
        ShellSection section,
        Type expectedPageType,
        bool expectedPaneVisibility,
        string expectedSelection,
        string? expectedMenuTag)
    {
        var target = ShellNavigationMap.GetTarget(section);

        Assert.Equal(expectedPageType, target.PageType);
        Assert.Equal(expectedPaneVisibility, target.IsPaneVisible);
        Assert.Equal(expectedSelection, target.Selection.ToString());
        Assert.Equal(expectedMenuTag, target.MenuTag);
        Assert.True(ShellNavigationMap.TryGetSection(target.PageType, out var roundTripSection));
        Assert.Equal(section, roundTripSection);
    }

    [Theory]
    [InlineData("Dashboard", ShellSection.Dashboard)]
    [InlineData("Assistant", ShellSection.Assistant)]
    [InlineData("Community", ShellSection.Community)]
    [InlineData("ModelSources", ShellSection.ModelSources)]
    [InlineData("Logs", ShellSection.Logs)]
    public void MenuTagsMapToInvokableSections(string tag, ShellSection expected)
    {
        Assert.True(ShellNavigationMap.TryGetMenuSection(tag, out var section));
        Assert.Equal(expected, section);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dashboard")]
    [InlineData("Onboarding")]
    [InlineData("Settings")]
    [InlineData("Unknown")]
    public void NonMenuTagsAreRejected(string? tag) =>
        Assert.False(ShellNavigationMap.TryGetMenuSection(tag, out _));

    [Fact]
    public void UnknownPageTypeIsRejected() =>
        Assert.False(ShellNavigationMap.TryGetSection(typeof(ShellNavigationMapTests), out _));

    [Fact]
    public void UnknownSectionIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ShellNavigationMap.GetTarget((ShellSection)999));
}
