using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LocaleSmith.App.Controls;

public sealed partial class ModArtifactDownloadControl : UserControl
{
    public ModArtifactDownloadControl()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<ModArtifactDownloadViewModel>();
        DataContext = ViewModel;
    }

    internal ModArtifactDownloadViewModel ViewModel { get; }

    internal Task SetArtifactAsync(
        ModPlatformVersion? artifact,
        CancellationToken cancellationToken = default) =>
        ViewModel.SelectArtifactAsync(artifact, cancellationToken);

    internal void CancelActiveOperation() => ViewModel.CancelActiveOperation();

    private async void OnDefaultDownloadClicked(object sender, RoutedEventArgs args) =>
        await PickAndDownloadAsync(ModPlatformDownloadRoute.Default).ConfigureAwait(true);

    private async void OnAcceleratedDownloadClicked(object sender, RoutedEventArgs args) =>
        await PickAndDownloadAsync(ModPlatformDownloadRoute.DomesticAcceleration).ConfigureAwait(true);

    private void OnCancelDownloadClicked(object sender, RoutedEventArgs args) =>
        ViewModel.CancelActiveOperation();

    private async Task PickAndDownloadAsync(ModPlatformDownloadRoute route)
    {
        if (ViewModel.Artifact is not { } artifact || ViewModel.IsBusy)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = artifact.Filename,
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        var extension = Path.GetExtension(artifact.Filename).ToLowerInvariant();
        if (extension is not (".jar" or ".zip"))
        {
            extension = ".jar";
        }

        picker.FileTypeChoices.Add("Minecraft artifact", [extension]);
        var window = App.MainWindow;
        if (window is null)
        {
            return;
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var destination = await picker.PickSaveFileAsync();
        if (destination is null)
        {
            return;
        }

        await ViewModel.DownloadAsync(destination.Path, route).ConfigureAwait(true);
    }
}
