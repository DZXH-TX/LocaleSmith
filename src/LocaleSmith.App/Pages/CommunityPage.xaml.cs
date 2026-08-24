using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LocaleSmith.App.Services;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace LocaleSmith.App.Pages;

public sealed partial class CommunityPage : Page
{
    private CommunityViewModel ViewModel { get; }
    private MicrosoftStoreBillingViewModel BillingViewModel { get; }
    private readonly NavigationInitializationCoordinator _initialization = new();
    private bool _deletePatDialogOpen;
    private bool _reportContentDialogOpen;
    private bool _reportAccessDialogOpen;
    private bool _reportUnavailableDialogOpen;
    private bool _allowReportDialogClose;
    private CommunityReportTarget? _pendingReportTarget;

    public CommunityPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<CommunityViewModel>();
        BillingViewModel = App.Services.GetRequiredService<MicrosoftStoreBillingViewModel>();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await _initialization.ActivateAsync(InitializeAsync).ConfigureAwait(true);

        async Task<bool> InitializeAsync()
        {
            await ViewModel.InitializeAsync().ConfigureAwait(true);
            return ViewModel.IsInitialized;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _initialization.Deactivate();
        ViewModel.CancelActiveOperation();
        ArtifactDownloadControl.CancelActiveOperation();
        if (_reportContentDialogOpen)
        {
            _allowReportDialogClose = true;
            TryHideDialog(ReportContentDialog);
        }

        if (_reportAccessDialogOpen)
        {
            TryHideDialog(ReportAccessDialog);
        }

        if (_reportUnavailableDialogOpen)
        {
            TryHideDialog(ReportUnavailableDialog);
        }

        if (_deletePatDialogOpen)
        {
            TryHideDialog(DeletePatDialog);
        }

        base.OnNavigatedFrom(e);
    }

    private async void OnModSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is Selector { SelectedItem: ModPlatformModSummary mod })
        {
            await ViewModel.SelectModAsync(mod).ConfigureAwait(true);
            await ArtifactDownloadControl.SetArtifactAsync(mod.LatestVersion).ConfigureAwait(true);
        }
    }

    private async void OnThreadSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is Selector { SelectedItem: ModPlatformForumThread thread })
        {
            await ViewModel.SelectThreadAsync(thread).ConfigureAwait(true);
        }
    }

    private void OnSearchInputKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && ViewModel.SearchCommand.CanExecute(null))
        {
            args.Handled = true;
            ViewModel.SearchCommand.Execute(null);
        }
    }

    private async void OnApplicationLoginClicked(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.CanSavePat)
        {
            return;
        }

        var passwordCharacters = CommunityPasswordInput.Password.ToCharArray();
        var tokenCharacters = CommunityPatInput.Password.ToCharArray();
        var authenticated = false;
        CommunityPasswordInput.Password = string.Empty;
        CommunityPatInput.Password = string.Empty;
        try
        {
            await ViewModel.SignInAsync(
                    CommunityUsernameInput.Text,
                    passwordCharacters,
                    tokenCharacters)
                .ConfigureAwait(true);
            if (ViewModel.IsAuthenticated)
            {
                CommunityUsernameInput.Text = string.Empty;
                authenticated = true;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(passwordCharacters.AsSpan()));
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(tokenCharacters.AsSpan()));
        }

        if (authenticated)
        {
            await BillingViewModel.RefreshAsync().ConfigureAwait(true);
            await ArtifactDownloadControl
                .SetArtifactAsync(ViewModel.SelectedMod?.LatestVersion)
                .ConfigureAwait(true);
        }
    }

    private async void OnDeletePatClicked(object sender, RoutedEventArgs args)
    {
        if (_deletePatDialogOpen || !ViewModel.DeletePatCommand.CanExecute(null))
        {
            return;
        }

        _deletePatDialogOpen = true;
        try
        {
            if (await DeletePatDialog.ShowAsync() == ContentDialogResult.Primary &&
                ViewModel.DeletePatCommand.CanExecute(null))
            {
                await ViewModel.DeletePatCommand.ExecuteAsync(null).ConfigureAwait(true);
                await BillingViewModel.RefreshAsync().ConfigureAwait(true);
                await ArtifactDownloadControl
                    .SetArtifactAsync(ViewModel.SelectedMod?.LatestVersion)
                    .ConfigureAwait(true);
            }
        }
        finally
        {
            _deletePatDialogOpen = false;
        }
    }

    private async void OnReportModClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ModPlatformModSummary mod })
        {
            await OpenReportDialogAsync(new CommunityReportTarget(
                ModPlatformReportTargetTypes.Mod,
                mod.Id,
                mod.Title)).ConfigureAwait(true);
        }
    }

    private async void OnReportVersionClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement
            {
                DataContext: ModPlatformModSummary
                {
                    LatestVersion: ModPlatformVersion version
                } mod
            })
        {
            await OpenReportDialogAsync(new CommunityReportTarget(
                ModPlatformReportTargetTypes.ModVersion,
                version.Id,
                $"{mod.Title} — {version.VersionName}")).ConfigureAwait(true);
        }
    }

    private async void OnReportModOwnerClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ModPlatformModSummary mod })
        {
            return;
        }

        await OpenUserReportDialogAsync(mod.OwnerId, mod.OwnerName).ConfigureAwait(true);
    }

    private async void OnReportSelectedModClicked(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedMod is { } mod)
        {
            await OpenReportDialogAsync(new CommunityReportTarget(
                ModPlatformReportTargetTypes.Mod,
                mod.Id,
                mod.Title)).ConfigureAwait(true);
        }
    }

    private async void OnReportSelectedModVersionClicked(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedMod is { LatestVersion: { } version } mod)
        {
            await OpenReportDialogAsync(new CommunityReportTarget(
                ModPlatformReportTargetTypes.ModVersion,
                version.Id,
                $"{mod.Title} — {version.VersionName}")).ConfigureAwait(true);
        }
    }

    private async void OnReportSelectedModOwnerClicked(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedMod is { } mod)
        {
            await OpenUserReportDialogAsync(mod.OwnerId, mod.OwnerName).ConfigureAwait(true);
        }
    }

    private async void OnReportThreadClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ModPlatformForumThread thread })
        {
            await OpenReportDialogAsync(new CommunityReportTarget(
                ModPlatformReportTargetTypes.ForumThread,
                thread.Id,
                thread.Title)).ConfigureAwait(true);
        }
    }

    private async void OnReportThreadAuthorClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ModPlatformForumThread thread })
        {
            await OpenUserReportDialogAsync(thread.AuthorId, thread.AuthorName).ConfigureAwait(true);
        }
    }

    private async void OnReportSelectedThreadClicked(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedThread is { } thread)
        {
            await OpenReportDialogAsync(new CommunityReportTarget(
                ModPlatformReportTargetTypes.ForumThread,
                thread.Id,
                thread.Title)).ConfigureAwait(true);
        }
    }

    private async void OnReportSelectedThreadAuthorClicked(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedThread is { } thread)
        {
            await OpenUserReportDialogAsync(thread.AuthorId, thread.AuthorName).ConfigureAwait(true);
        }
    }

    private async void OnReportPostClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ModPlatformForumPost post })
        {
            await OpenReportDialogAsync(new CommunityReportTarget(
                ModPlatformReportTargetTypes.ForumPost,
                post.Id,
                post.AuthorName)).ConfigureAwait(true);
        }
    }

    private async void OnReportPostAuthorClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ModPlatformForumPost post })
        {
            await OpenUserReportDialogAsync(post.AuthorId, post.AuthorName).ConfigureAwait(true);
        }
    }

    private async Task OpenUserReportDialogAsync(Guid? userId, string displayName)
    {
        if (userId is not { } id || id == Guid.Empty)
        {
            await ShowReportUnavailableDialogAsync().ConfigureAwait(true);
            return;
        }

        await OpenReportDialogAsync(new CommunityReportTarget(
            ModPlatformReportTargetTypes.User,
            id,
            displayName)).ConfigureAwait(true);
    }

    private async Task OpenReportDialogAsync(CommunityReportTarget target)
    {
        if (IsAnyReportDialogOpen())
        {
            return;
        }

        if (!ViewModel.IsReportFeatureAvailable || !ViewModel.SupportsReportTarget(target.TargetType))
        {
            await ShowReportUnavailableDialogAsync().ConfigureAwait(true);
            return;
        }

        if (!ViewModel.IsAuthenticated || !ViewModel.HasReportPermission)
        {
            await ShowReportAccessDialogAsync().ConfigureAwait(true);
            return;
        }

        _pendingReportTarget = target;
        ViewModel.ResetReportFeedback();
        ReportTargetText.Text = target.DisplayName;
        ReportCategoryInput.SelectedItem = null;
        ReportDetailsInput.Text = string.Empty;
        UpdateReportPrimaryButtonState();
        _allowReportDialogClose = false;
        _reportContentDialogOpen = true;
        try
        {
            await ReportContentDialog.ShowAsync();
        }
        finally
        {
            ReportCategoryInput.SelectedItem = null;
            ReportDetailsInput.Text = string.Empty;
            ReportTargetText.Text = string.Empty;
            _pendingReportTarget = null;
            _reportContentDialogOpen = false;
            _allowReportDialogClose = false;
            ViewModel.ResetReportFeedback();
        }
    }

    private async Task ShowReportAccessDialogAsync()
    {
        if (IsAnyReportDialogOpen())
        {
            return;
        }

        _reportAccessDialogOpen = true;
        try
        {
            await ReportAccessDialog.ShowAsync();
        }
        finally
        {
            _reportAccessDialogOpen = false;
        }
    }

    private async Task ShowReportUnavailableDialogAsync()
    {
        if (IsAnyReportDialogOpen())
        {
            return;
        }

        _reportUnavailableDialogOpen = true;
        try
        {
            await ReportUnavailableDialog.ShowAsync();
        }
        finally
        {
            _reportUnavailableDialogOpen = false;
        }
    }

    private async void OnReportPrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = true;
        ReportCategoryInput.IsEnabled = false;
        ReportDetailsInput.IsEnabled = false;
        sender.IsPrimaryButtonEnabled = false;
        try
        {
            var category = (ReportCategoryInput.SelectedItem as CommunityReportCategoryOption)?.Code;
            var submitted = await ViewModel.SubmitReportAsync(
                    _pendingReportTarget,
                    category,
                    ReportDetailsInput.Text)
                .ConfigureAwait(true);
            args.Cancel = !submitted;
        }
        finally
        {
            ReportCategoryInput.IsEnabled = true;
            ReportDetailsInput.IsEnabled = true;
            if (args.Cancel)
            {
                UpdateReportPrimaryButtonState();
            }

            deferral.Complete();
        }
    }

    private void OnReportDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (ShouldBlockReportDialogClosing(ViewModel.IsReportSubmitting, _allowReportDialogClose))
        {
            args.Cancel = true;
        }
    }

    internal static bool ShouldBlockReportDialogClosing(
        bool isReportSubmitting,
        bool allowForcedClose) =>
        isReportSubmitting && !allowForcedClose;

    private void OnReportCategoryChanged(object sender, SelectionChangedEventArgs args) =>
        UpdateReportPrimaryButtonState();

    private void OnReportDetailsChanged(object sender, TextChangedEventArgs args) =>
        UpdateReportPrimaryButtonState();

    private void UpdateReportPrimaryButtonState()
    {
        var category = (ReportCategoryInput.SelectedItem as CommunityReportCategoryOption)?.Code;
        ReportContentDialog.IsPrimaryButtonEnabled = ViewModel.IsReportInputValid(
            _pendingReportTarget,
            category,
            ReportDetailsInput.Text);
    }

    private bool IsAnyReportDialogOpen() =>
        _reportContentDialogOpen || _reportAccessDialogOpen || _reportUnavailableDialogOpen;

    private static void TryHideDialog(ContentDialog dialog)
    {
        try
        {
            dialog.Hide();
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not AccessViolationException)
        {
            // Dialog state may already be transitioning during navigation.
        }
    }
}
