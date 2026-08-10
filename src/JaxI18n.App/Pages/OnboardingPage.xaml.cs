using JaxI18n.Presentation.Models;
using JaxI18n.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JaxI18n.App.Pages;

public sealed partial class OnboardingPage : Page
{
    private OnboardingViewModel ViewModel { get; }
    private bool _secretEventSubscribed;

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
        if (!_secretEventSubscribed)
        {
            ViewModel.SecretInputConsumed += OnSecretInputConsumed;
            _secretEventSubscribed = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_secretEventSubscribed)
        {
            ViewModel.SecretInputConsumed -= OnSecretInputConsumed;
            _secretEventSubscribed = false;
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

    private void OnSecretInputConsumed(object? sender, EventArgs args)
    {
        OnboardingNetworkApiKeyInput.Password = string.Empty;
        ViewModel.SetNetworkApiKeyPresent(false);
    }
}
