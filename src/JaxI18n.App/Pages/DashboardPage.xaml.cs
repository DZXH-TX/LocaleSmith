using JaxI18n.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace JaxI18n.App.Pages;

public sealed partial class DashboardPage : Page
{
    private DashboardViewModel ViewModel { get; }

    public DashboardPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<DashboardViewModel>();
        DataContext = ViewModel;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // The page is cached, while model sources can be edited from another navigation destination.
        ViewModel.RefreshModelSources();
    }

    private async void OnAddPackagesClicked(object sender, RoutedEventArgs args)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add(".jar");
        picker.FileTypeFilter.Add(".zip");
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        var files = await picker.PickMultipleFilesAsync();
        await ViewModel.EnqueuePackagesAsync(files.Select(static file => file.Path)).ConfigureAwait(true);
    }

    private async void OnAddFolderClicked(object sender, RoutedEventArgs args)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await ViewModel.EnqueuePackagesAsync([folder.Path]).ConfigureAwait(true);
        }
    }

    private void OnCancelQueueItemClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: QueueItemViewModel item })
        {
            ViewModel.CancelCommand.Execute(item);
        }
    }

    private static nint GetWindowHandle() => App.MainWindow is null
        ? throw new InvalidOperationException("The main window is unavailable.")
        : WindowNative.GetWindowHandle(App.MainWindow);
}
