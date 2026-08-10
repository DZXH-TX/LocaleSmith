using LocaleSmith.App.Pages;
using LocaleSmith.App.Services;
using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace LocaleSmith.App;

public sealed partial class MainWindow : Window
{
    private readonly ShellViewModel? _viewModel;
    private readonly AppMotionService? _motion;
    private bool _chromeSynchronizationQueued;
    private bool _isClosed;

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
        Closed += OnClosedForChromeSynchronization;
        Activated += OnActivated;
    }

    public MainWindow(string startupError)
    {
        InitializeComponent();
        RootSurface.ActualThemeChanged += OnActualThemeChanged;
        Closed += OnClosedForChromeSynchronization;
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
        var requestedTheme = theme switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        RootSurface.RequestedTheme = requestedTheme;
        RootNavigation.RequestedTheme = requestedTheme;
        QueueWindowChromeSynchronization();
    }

    private void OnNavigationRequested(object? sender, ShellSection section)
    {
        var target = ShellNavigationMap.GetTarget(section);
        if (ContentFrame.CurrentSourcePageType == target.PageType)
        {
            ApplyNavigationPresentation(section);
            return;
        }

        if (ContentFrame.Content is FrameworkElement outgoingPage)
        {
            _motion?.Cancel(outgoingPage);
        }

        if (!ContentFrame.Navigate(target.PageType, null, new SuppressNavigationTransitionInfo()))
        {
            SynchronizeNavigationPresentationWithFrame();
        }
    }

    private void OnContentFrameNavigated(object sender, NavigationEventArgs args)
    {
        if (ShellNavigationMap.TryGetSection(args.SourcePageType, out var section))
        {
            ApplyNavigationPresentation(section);
        }

        if (args.Content is FrameworkElement incomingPage)
        {
            _motion?.AnimatePageEntrance(incomingPage);
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) =>
        QueueWindowChromeSynchronization();

    private void OnRootSurfaceLoaded(object sender, RoutedEventArgs args) =>
        QueueWindowChromeSynchronization();

    private void QueueWindowChromeSynchronization()
    {
        if (_isClosed || _chromeSynchronizationQueued)
        {
            return;
        }

        _chromeSynchronizationQueued = true;
        if (!RootSurface.DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    _chromeSynchronizationQueued = false;
                    if (!_isClosed)
                    {
                        SynchronizeWindowChrome();
                    }
                }))
        {
            _chromeSynchronizationQueued = false;
        }
    }

    private void OnClosedForChromeSynchronization(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        RootSurface.ActualThemeChanged -= OnActualThemeChanged;
        Closed -= OnClosedForChromeSynchronization;
    }

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
            SynchronizeNavigationPresentationWithFrame();
            return;
        }

        if (ShellNavigationMap.TryGetMenuSection(
                args.InvokedItemContainer?.Tag as string,
                out var section))
        {
            _viewModel.NavigateCommand.Execute(section);
        }

        // NavigationView updates its visual selection before the command runs. If navigation is
        // temporarily unavailable (for example while encrypted settings are still loading), put
        // the selection back on the page that is actually displayed.
        SynchronizeNavigationPresentationWithFrame();
    }

    private void SynchronizeNavigationPresentationWithFrame()
    {
        if (ShellNavigationMap.TryGetSection(ContentFrame.CurrentSourcePageType, out var section))
        {
            ApplyNavigationPresentation(section);
        }
    }

    private void ApplyNavigationPresentation(ShellSection section)
    {
        var target = ShellNavigationMap.GetTarget(section);
        RootNavigation.IsPaneVisible = target.IsPaneVisible;
        object? selectedItem = target.Selection switch
        {
            ShellNavigationSelection.None => null,
            ShellNavigationSelection.Settings => RootNavigation.SettingsItem,
            ShellNavigationSelection.MenuItem => FindNavigationItem(target.MenuTag),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown selection behavior.")
        };

        if (!ReferenceEquals(RootNavigation.SelectedItem, selectedItem))
        {
            RootNavigation.SelectedItem = selectedItem;
        }
    }

    private object? FindNavigationItem(string? tag)
    {
        foreach (var item in RootNavigation.MenuItems)
        {
            if (item is NavigationViewItem { Tag: string itemTag } &&
                string.Equals(itemTag, tag, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }
}
