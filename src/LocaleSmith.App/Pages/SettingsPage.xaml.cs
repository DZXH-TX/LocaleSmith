using LocaleSmith.App.Services;
using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LocaleSmith.App.Pages;

public sealed partial class SettingsPage : Page
{
    private SettingsViewModel ViewModel { get; }
    private bool _loaded;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        DataContext = ViewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
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

    private void OnForceAppAnimationsToggled(object sender, RoutedEventArgs args)
    {
        if (_loaded && sender is ToggleSwitch toggle)
        {
            ApplyMotionPreference(toggle.IsOn);
        }
    }

    private async void OnLanguageRestartClicked(object sender, RoutedEventArgs args)
    {
        if (!_loaded || !ViewModel.IsLanguageRestartRequired || ViewModel.IsBusy)
        {
            return;
        }

        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            if (!ViewModel.SaveCommand.CanExecute(null))
            {
                return;
            }

            await ViewModel.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
            if (ViewModel.HasError)
            {
                return;
            }

            var failureReason = App.RestartWithDisplayLanguage(ViewModel.Language);
            ViewModel.ReportLanguageRestartFailure(failureReason);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ViewModel.ReportLanguageRestartFailure(exception.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static void ApplyMotionPreference(bool forceAppAnimations)
    {
        App.Services.GetRequiredService<AppMotionService>()
            .SetForceAppAnimations(forceAppAnimations);
    }

    private async void OnBrowseLogDirectoryClicked(object sender, RoutedEventArgs args)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.LogDirectoryPath = folder.Path;
        }
    }

    private static nint GetWindowHandle() => App.MainWindow is null
        ? throw new InvalidOperationException("The main window is unavailable.")
        : WindowNative.GetWindowHandle(App.MainWindow);
}
