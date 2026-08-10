using LocaleSmith.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LocaleSmith.App.Dialogs;

public sealed partial class CliConfirmationDialog : ContentDialog
{
    public CliConfirmationDialog(CliConfirmationViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;
        if (ViewModel.IsPolicyDenied)
        {
            PrimaryButtonText = string.Empty;
        }
    }

    public CliConfirmationViewModel ViewModel { get; }

    private async void OnPrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            await ViewModel.ExecuteAsync().ConfigureAwait(true);
            IsPrimaryButtonEnabled = ViewModel.CanExecute;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!ViewModel.HasResult)
        {
            ViewModel.CancelCommand.Execute(null);
        }
    }
}
