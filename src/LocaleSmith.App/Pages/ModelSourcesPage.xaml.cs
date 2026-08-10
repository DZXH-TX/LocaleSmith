using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LocaleSmith.App.Pages;

public sealed partial class ModelSourcesPage : Page
{
    private ModelSourcesViewModel ViewModel { get; }
    private bool _loaded;
    private bool _loading;

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
        if (_loaded || _loading)
        {
            return;
        }

        _loading = true;
        try
        {
            await ViewModel.LoadAsync().ConfigureAwait(true);
            _loaded = true;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnPresetSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loaded || sender is not ComboBox comboBox)
        {
            return;
        }

        if (ModelOptionSelectionMap.TryResolvePreset(
                comboBox.SelectedItem,
                ViewModel.PresetOptions,
                out var preset) &&
            !ReferenceEquals(ViewModel.SelectedPreset, preset))
        {
            ViewModel.SelectedPreset = preset;
        }
    }

    private void OnTokenLimitParameterSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loaded || sender is not ComboBox comboBox)
        {
            return;
        }

        if (ModelOptionSelectionMap.TryResolveTokenLimitParameter(
                comboBox.SelectedItem,
                ViewModel.TokenLimitParameterOptions,
                out var option) &&
            !ReferenceEquals(ViewModel.SelectedTokenLimitParameterOption, option))
        {
            ViewModel.SelectedTokenLimitParameterOption = option;
        }
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
