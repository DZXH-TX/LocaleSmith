using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LocaleSmith.App.Pages;

public sealed partial class OnboardingPage : Page
{
    private OnboardingViewModel ViewModel { get; }
    private bool _secretEventSubscribed;
    private bool _loaded;

    public OnboardingPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<OnboardingViewModel>();
        DataContext = ViewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _loaded = true;
        if (!_secretEventSubscribed)
        {
            ViewModel.SecretInputConsumed += OnSecretInputConsumed;
            _secretEventSubscribed = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _loaded = false;
        if (_secretEventSubscribed)
        {
            ViewModel.SecretInputConsumed -= OnSecretInputConsumed;
            _secretEventSubscribed = false;
        }
    }

    private void OnNetworkTokenLimitParameterSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loaded || sender is not ComboBox comboBox)
        {
            return;
        }

        if (ModelOptionSelectionMap.TryResolveTokenLimitParameter(
                comboBox.SelectedItem,
                ViewModel.NetworkTokenLimitParameterOptions,
                out var option) &&
            !ReferenceEquals(ViewModel.NetworkTokenLimitParameterOption, option))
        {
            ViewModel.NetworkTokenLimitParameterOption = option;
        }
    }

    private void OnModelPathChecked(object sender, RoutedEventArgs args)
    {
        if (sender is RadioButton { IsChecked: true, Tag: string tag } &&
            Enum.TryParse<OnboardingModelPath>(tag, ignoreCase: false, out var path))
        {
            ViewModel.SelectModelPath(path);
        }
    }

    private void OnNetworkApiKeyChanged(object sender, RoutedEventArgs args)
    {
        if (sender is PasswordBox passwordBox)
        {
            ViewModel.SetNetworkApiKeyPresent(!string.IsNullOrWhiteSpace(passwordBox.Password));
        }
    }

    private async void OnLanguageRestartClicked(object sender, RoutedEventArgs args)
    {
        if (!_loaded || !ViewModel.IsLanguageRestartRequired || ViewModel.IsBusy)
        {
            return;
        }

        OnboardingLanguageSelector.IsEnabled = false;
        OnboardingLanguageRestartButton.IsEnabled = false;
        try
        {
            if (!await ViewModel.SaveDisplayLanguageForRestartAsync().ConfigureAwait(true) ||
                !ViewModel.TryGetPersistedDisplayLanguageForRestart(out var persistedLanguage))
            {
                return;
            }

            var failureReason = App.RestartWithDisplayLanguage(persistedLanguage);
            ViewModel.ReportLanguageRestartFailure(failureReason);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ViewModel.ReportLanguageRestartFailure(exception.Message);
        }
        finally
        {
            OnboardingLanguageSelector.IsEnabled = true;
            OnboardingLanguageRestartButton.IsEnabled = true;
        }
    }

    private void OnSecretInputConsumed(object? sender, EventArgs args)
    {
        OnboardingNetworkApiKeyInput.Password = string.Empty;
        ViewModel.SetNetworkApiKeyPresent(false);
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
