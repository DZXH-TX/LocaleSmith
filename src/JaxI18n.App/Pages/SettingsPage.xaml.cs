using JaxI18n.App.Dialogs;
using JaxI18n.App.Services;
using JaxI18n.Presentation.Models;
using JaxI18n.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization;

namespace JaxI18n.App.Pages;

public sealed partial class SettingsPage : Page
{
    private SettingsViewModel ViewModel { get; }
    private bool _loaded;
    private bool _loading;
    private bool _confirmationSubscribed;

    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        DataContext = ViewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!_confirmationSubscribed)
        {
            ViewModel.CliConfirmationRequested += OnCliConfirmationRequested;
            _confirmationSubscribed = true;
        }

        if (_loaded || _loading)
        {
            return;
        }

        _loading = true;
        try
        {
            await ViewModel.LoadAsync().ConfigureAwait(true);
            _loaded = true;
            App.MainWindow?.ApplyTheme(ViewModel.Theme);
            ApplyMotionPreference(ViewModel.ForceAppAnimations);
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loaded && sender is ComboBox { SelectedItem: AppThemePreference theme })
        {
            App.MainWindow?.ApplyTheme(theme);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_confirmationSubscribed)
        {
            ViewModel.CliConfirmationRequested -= OnCliConfirmationRequested;
            _confirmationSubscribed = false;
        }
    }

    private void OnForceAppAnimationsToggled(object sender, RoutedEventArgs args)
    {
        if (_loaded && sender is ToggleSwitch toggle)
        {
            ApplyMotionPreference(toggle.IsOn);
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loaded && sender is ComboBox { SelectedItem: string language })
        {
            ApplicationLanguages.PrimaryLanguageOverride = language;
        }
    }

    private static void ApplyMotionPreference(bool forceAppAnimations)
    {
        App.Services.GetRequiredService<AppMotionService>()
            .SetForceAppAnimations(forceAppAnimations);
    }

    private async void OnCliConfirmationRequested(
        object? sender,
        CliConfirmationRequestedEventArgs args)
    {
        var dialog = new CliConfirmationDialog(args.Confirmation)
        {
            XamlRoot = XamlRoot
        };
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            args.Confirmation.Dispose();
        }
    }
}
