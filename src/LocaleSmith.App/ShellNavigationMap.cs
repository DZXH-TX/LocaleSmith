using LocaleSmith.App.Pages;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App;

internal enum ShellNavigationSelection
{
    None,
    MenuItem,
    Settings
}

internal readonly record struct ShellNavigationTarget(
    Type PageType,
    bool IsPaneVisible,
    ShellNavigationSelection Selection,
    string? MenuTag = null);

internal static class ShellNavigationMap
{
    public static ShellNavigationTarget GetTarget(ShellSection section) => section switch
    {
        ShellSection.Onboarding => new(
            typeof(OnboardingPage),
            IsPaneVisible: false,
            ShellNavigationSelection.None),
        ShellSection.Dashboard => MenuTarget<DashboardPage>(nameof(ShellSection.Dashboard)),
        ShellSection.Assistant => MenuTarget<AssistantPage>(nameof(ShellSection.Assistant)),
        ShellSection.ModelSources => MenuTarget<ModelSourcesPage>(nameof(ShellSection.ModelSources)),
        ShellSection.Logs => MenuTarget<LogsPage>(nameof(ShellSection.Logs)),
        ShellSection.Settings => new(
            typeof(SettingsPage),
            IsPaneVisible: true,
            ShellNavigationSelection.Settings),
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown shell section.")
    };

    public static bool TryGetSection(Type? pageType, out ShellSection section)
    {
        section = pageType switch
        {
            var type when type == typeof(OnboardingPage) => ShellSection.Onboarding,
            var type when type == typeof(DashboardPage) => ShellSection.Dashboard,
            var type when type == typeof(AssistantPage) => ShellSection.Assistant,
            var type when type == typeof(ModelSourcesPage) => ShellSection.ModelSources,
            var type when type == typeof(LogsPage) => ShellSection.Logs,
            var type when type == typeof(SettingsPage) => ShellSection.Settings,
            _ => default
        };
        return pageType is not null && GetTarget(section).PageType == pageType;
    }

    public static bool TryGetMenuSection(string? tag, out ShellSection section)
    {
        section = tag switch
        {
            nameof(ShellSection.Dashboard) => ShellSection.Dashboard,
            nameof(ShellSection.Assistant) => ShellSection.Assistant,
            nameof(ShellSection.ModelSources) => ShellSection.ModelSources,
            nameof(ShellSection.Logs) => ShellSection.Logs,
            _ => default
        };
        return tag is nameof(ShellSection.Dashboard)
            or nameof(ShellSection.Assistant)
            or nameof(ShellSection.ModelSources)
            or nameof(ShellSection.Logs);
    }

    private static ShellNavigationTarget MenuTarget<TPage>(string tag) => new(
        typeof(TPage),
        IsPaneVisible: true,
        ShellNavigationSelection.MenuItem,
        tag);
}
