using JaxI18n.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JaxI18n.App.Pages;

public sealed partial class ModelSourcesPage : Page
{
    private ModelSourcesViewModel ViewModel { get; }
    private bool _loaded;

    public ModelSourcesPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<ModelSourcesViewModel>();
        ViewModel.SecretInputConsumed += OnSecretInputConsumed;
        DataContext = ViewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await ViewModel.LoadAsync().ConfigureAwait(true);
    }

    private void OnSecretInputConsumed(object? sender, EventArgs args) => ApiKeyInput.Password = string.Empty;

    private async void OnDeleteClicked(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedSource is null)
        {
            return;
        }

        var result = await DeleteDialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteSelectedAsync().ConfigureAwait(true);
        }
    }
}
