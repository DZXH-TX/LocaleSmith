using JaxI18n.App.Pages;
using JaxI18n.App.Services;
using JaxI18n.Presentation.Models;
using JaxI18n.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace JaxI18n.App;

public sealed partial class MainWindow : Window
{
    private readonly ShellViewModel? _viewModel;
    private readonly AppMotionService? _motion;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<ShellViewModel>();
        _motion = App.Services.GetRequiredService<AppMotionService>();
        _viewModel.NavigationRequested += OnNavigationRequested;
        var onboarding = App.Services.GetRequiredService<OnboardingViewModel>();
        onboarding.Completed += OnOnboardingCompleted;
        onboarding.ExitRequested += OnExitRequested;
        ContentFrame.Navigated += OnContentFrameNavigated;
        RootSurface.ActualThemeChanged += OnActualThemeChanged;
        Activated += OnActivated;
    }

    public MainWindow(string startupError)
    {
        InitializeComponent();
        RootSurface.ActualThemeChanged += OnActualThemeChanged;
        RootNavigation.Visibility = Visibility.Collapsed;
        FatalErrorPanel.Visibility = Visibility.Visible;
        FatalErrorText.Text = startupError;
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;
        if (_viewModel is not null)
        {
            await _viewModel.InitializeAsync().ConfigureAwait(true);
            ApplyTheme(_viewModel.Theme);
        }
    }

    public void ApplyTheme(AppThemePreference theme)
    {
        RootSurface.RequestedTheme = theme switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        RootSurface.DispatcherQueue.TryEnqueue(SynchronizeWindowChrome);
    }

    private void OnNavigationRequested(object? sender, ShellSection section)
    {
        RootNavigation.IsPaneVisible = section != ShellSection.Onboarding;
        var pageType = section switch
        {
            ShellSection.Onboarding => typeof(OnboardingPage),
            ShellSection.Dashboard => typeof(DashboardPage),
            ShellSection.Assistant => typeof(AssistantPage),
            ShellSection.ModelSources => typeof(ModelSourcesPage),
            ShellSection.Settings => typeof(SettingsPage),
            _ => typeof(DashboardPage)
        };
        if (ContentFrame.CurrentSourcePageType == pageType)
        {
            return;
        }

        if (ContentFrame.Content is FrameworkElement outgoingPage)
        {
            _motion?.Cancel(outgoingPage);
        }

        ContentFrame.Navigate(pageType, null, new SuppressNavigationTransitionInfo());
    }

    private void OnContentFrameNavigated(object sender, NavigationEventArgs args)
    {
        if (args.Content is FrameworkElement incomingPage)
        {
            _motion?.AnimatePageEntrance(incomingPage);
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) =>
        SynchronizeWindowChrome();

    private void OnRootSurfaceLoaded(object sender, RoutedEventArgs args) =>
        SynchronizeWindowChrome();

    private void SynchronizeWindowChrome()
    {
        if (TitleBarBackgroundProbe.Background is not SolidColorBrush background ||
            TitleBarForegroundProbe.Foreground is not SolidColorBrush foreground ||
            TitleBarHoverProbe.Background is not SolidColorBrush hover ||
            TitleBarPressedProbe.Background is not SolidColorBrush pressed ||
            TitleBarInactiveForegroundProbe.Foreground is not SolidColorBrush inactiveForeground)
        {
            return;
        }

        // NavigationView uses separate brushes for expanded, compact/overlay, and top panes.
        // Keep all of them on the same semantic surface as the window and content host.
        RootNavigation.Resources["NavigationViewExpandedPaneBackground"] =
            new SolidColorBrush(background.Color);
        RootNavigation.Resources["NavigationViewDefaultPaneBackground"] =
            new SolidColorBrush(background.Color);
        RootNavigation.Resources["NavigationViewTopPaneBackground"] =
            new SolidColorBrush(background.Color);

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredTheme = RootSurface.ActualTheme == ElementTheme.Light
            ? TitleBarTheme.Light
            : TitleBarTheme.Dark;
        titleBar.BackgroundColor = background.Color;
        titleBar.ForegroundColor = foreground.Color;
        titleBar.InactiveBackgroundColor = background.Color;
        titleBar.InactiveForegroundColor = inactiveForeground.Color;
        titleBar.ButtonBackgroundColor = background.Color;
        titleBar.ButtonForegroundColor = foreground.Color;
        titleBar.ButtonHoverBackgroundColor = hover.Color;
        titleBar.ButtonHoverForegroundColor = foreground.Color;
        titleBar.ButtonPressedBackgroundColor = pressed.Color;
        titleBar.ButtonPressedForegroundColor = foreground.Color;
        titleBar.ButtonInactiveBackgroundColor = background.Color;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground.Color;
    }

    private void OnOnboardingCompleted(object? sender, EventArgs args) =>
        _viewModel?.CompleteOnboarding();

    private void OnExitRequested(object? sender, EventArgs args) => Close();

    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (args.IsSettingsInvoked)
        {
            _viewModel.NavigateCommand.Execute(ShellSection.Settings);
            return;
        }

        if (args.InvokedItemContainer?.Tag is string tag &&
            Enum.TryParse<ShellSection>(tag, ignoreCase: false, out var section))
        {
            _viewModel.NavigateCommand.Execute(section);
        }
    }
}
