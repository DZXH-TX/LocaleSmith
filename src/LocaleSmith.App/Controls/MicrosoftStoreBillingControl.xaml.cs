using LocaleSmith.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LocaleSmith.App.Controls;

public sealed partial class MicrosoftStoreBillingControl : UserControl
{
    public MicrosoftStoreBillingControl()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<MicrosoftStoreBillingViewModel>();
        DataContext = ViewModel;
    }

    internal MicrosoftStoreBillingViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        await ViewModel.InitializeAsync().ConfigureAwait(true);
    }
}
