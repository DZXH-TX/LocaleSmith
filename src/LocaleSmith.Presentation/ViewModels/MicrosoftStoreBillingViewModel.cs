using CommunityToolkit.Mvvm.Input;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Abstractions;

namespace LocaleSmith.Presentation.ViewModels;

/// <summary>
/// Coordinates the Microsoft purchase UI with MCTX verification. A Store result never sets the
/// entitlement flag; only GET /api/v1/me/entitlements can do that.
/// </summary>
public sealed class MicrosoftStoreBillingViewModel : ViewModelBase
{
    private readonly IModPlatformBillingClient _billingClient;
    private readonly IModPlatformCredentialService _credentials;
    private readonly IMicrosoftStorefront _storefront;
    private readonly IPrivilegeContext _privilegeContext;
    private readonly IUiTextProvider _text;
    private readonly TimeProvider _timeProvider;
    private bool _initialized;
    private bool _isCapabilityAvailable;
    private bool _isAuthenticated;
    private bool _hasAcceleratedDownloadScope;
    private bool _isBackendReady;
    private bool _isProductAvailable;
    private bool _hasActiveEntitlement;
    private bool _isStorePurchaseFound;
    private bool _offersSevenDayTrial;
    private Guid? _authenticatedUserId;
    private string? _formattedStorePrice;

    public MicrosoftStoreBillingViewModel(
        IModPlatformBillingClient billingClient,
        IModPlatformCredentialService credentials,
        IMicrosoftStorefront storefront,
        IPrivilegeContext privilegeContext,
        IUiTextProvider? text = null,
        TimeProvider? timeProvider = null)
    {
        _billingClient = billingClient ?? throw new ArgumentNullException(nameof(billingClient));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _storefront = storefront ?? throw new ArgumentNullException(nameof(storefront));
        _privilegeContext = privilegeContext ?? throw new ArgumentNullException(nameof(privilegeContext));
        _text = text ?? FallbackUiTextProvider.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;

        PurchaseCommand = new AsyncRelayCommand(PurchaseAsync, CanPurchase);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, CanUseStoreActions);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRefresh);
    }

    public IAsyncRelayCommand PurchaseCommand { get; }

    public IAsyncRelayCommand RestoreCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public Uri AccountUri { get; } = new("https://dow.dzxh-tx.cn/user/dashboard");

    public Uri ManageSubscriptionsUri { get; } = MicrosoftStoreBillingContract.ManageSubscriptionsUri;

    public Uri PrivacyPolicyUri { get; } = MicrosoftStoreBillingContract.PrivacyPolicyUri;

    public bool IsInitialized
    {
        get => _initialized;
        private set
        {
            if (SetProperty(ref _initialized, value))
            {
                NotifyPresentationChanged();
            }
        }
    }

    public bool IsCapabilityAvailable
    {
        get => _isCapabilityAvailable;
        private set
        {
            if (SetProperty(ref _isCapabilityAvailable, value))
            {
                NotifyPresentationChanged();
            }
        }
    }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set
        {
            if (SetProperty(ref _isAuthenticated, value))
            {
                NotifyPresentationChanged();
            }
        }
    }

    public bool IsBackendReady
    {
        get => _isBackendReady;
        private set
        {
            if (SetProperty(ref _isBackendReady, value))
            {
                NotifyPresentationChanged();
            }
        }
    }

    public bool HasAcceleratedDownloadScope
    {
        get => _hasAcceleratedDownloadScope;
        private set
        {
            if (SetProperty(ref _hasAcceleratedDownloadScope, value))
            {
                NotifyPresentationChanged();
            }
        }
    }

    public bool IsProductAvailable
    {
        get => _isProductAvailable;
        private set
        {
            if (SetProperty(ref _isProductAvailable, value))
            {
                NotifyPresentationChanged();
            }
        }
    }

    public bool HasActiveEntitlement
    {
        get => _hasActiveEntitlement;
        private set
        {
            if (SetProperty(ref _hasActiveEntitlement, value))
            {
                NotifyPresentationChanged();
            }
        }
    }

    public bool IsStorePurchaseFound
    {
        get => _isStorePurchaseFound;
        private set => SetProperty(ref _isStorePurchaseFound, value);
    }

    public bool OffersSevenDayTrial
    {
        get => _offersSevenDayTrial;
        private set => SetProperty(ref _offersSevenDayTrial, value);
    }

    public string? FormattedStorePrice
    {
        get => _formattedStorePrice;
        private set => SetProperty(ref _formattedStorePrice, value);
    }

    public bool IsElevated => _privilegeContext.IsElevated;

    public bool ShowCapabilityUnavailable => IsInitialized && !IsCapabilityAvailable;

    public bool ShowLoginRequired => IsCapabilityAvailable && !IsAuthenticated;

    public bool ShowScopeRequired =>
        IsCapabilityAvailable && IsAuthenticated && !HasAcceleratedDownloadScope;

    public bool ShowBackendUnavailable =>
        IsCapabilityAvailable && IsAuthenticated && HasAcceleratedDownloadScope && !IsBackendReady;

    public bool ShowStoreUnavailable =>
        IsCapabilityAvailable && IsAuthenticated && HasAcceleratedDownloadScope && IsBackendReady && !IsProductAvailable;

    public bool ShowActiveEntitlement =>
        IsCapabilityAvailable && IsAuthenticated && HasAcceleratedDownloadScope && IsBackendReady && HasActiveEntitlement;

    public bool ShowInactiveEntitlement =>
        IsCapabilityAvailable && IsAuthenticated && HasAcceleratedDownloadScope && IsBackendReady && !HasActiveEntitlement;

    public bool IsPurchaseEntryVisible =>
        IsCapabilityAvailable
        && IsAuthenticated
        && HasAcceleratedDownloadScope
        && IsBackendReady
        && IsProductAvailable
        && !HasActiveEntitlement
        && !IsElevated;

    public bool AreStoreActionsVisible =>
        IsCapabilityAvailable
        && IsAuthenticated
        && HasAcceleratedDownloadScope
        && IsBackendReady
        && !IsElevated;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitialized || IsBusy)
        {
            return;
        }

        await RefreshCoreAsync(cancellationToken).ConfigureAwait(true);
        IsInitialized = true;
    }

    private async Task PurchaseAsync()
    {
        if (!CanPurchase())
        {
            return;
        }

        await RunBusyAsync(async cancellationToken =>
        {
            if (!await RefreshAuthorizationAsync(cancellationToken).ConfigureAwait(true))
            {
                return;
            }

            await LoadEntitlementsAsync(cancellationToken).ConfigureAwait(true);
            if (!IsBackendReady || HasActiveEntitlement)
            {
                StatusMessage = HasActiveEntitlement
                    ? _text.GetText(
                        "BillingAlreadyEntitledStatus",
                        "The MCTX entitlement is already active; Microsoft purchase UI was not opened.")
                    : StatusMessage;
                return;
            }

            if (!await LoadProductAsync(cancellationToken).ConfigureAwait(true))
            {
                return;
            }

            var outcome = await _storefront
                .RequestSubscriptionPurchaseAsync(cancellationToken)
                .ConfigureAwait(true);
            switch (outcome.Status)
            {
                case MicrosoftStorePurchaseStatus.Succeeded:
                case MicrosoftStorePurchaseStatus.AlreadyPurchased:
                    StatusMessage = _text.GetText(
                        "BillingPurchasePendingVerificationStatus",
                        "Microsoft completed the purchase step. LocaleSmith is verifying the entitlement with MCTX.");
                    await ReconcileWithBackendAsync(cancellationToken).ConfigureAwait(true);
                    StatusMessage = HasActiveEntitlement
                        ? _text.GetText(
                            "BillingEntitlementVerifiedStatus",
                            "The MCTX entitlement is active.")
                        : _text.GetText(
                            "BillingEntitlementPendingStatus",
                            "Microsoft reported the purchase, but MCTX has not activated the entitlement yet. Use Restore purchase to retry verification.");
                    break;
                case MicrosoftStorePurchaseStatus.NotPurchased:
                    StatusMessage = _text.GetText(
                        "BillingPurchaseCancelledStatus",
                        "The Microsoft purchase was not completed. No entitlement changed.");
                    break;
                case MicrosoftStorePurchaseStatus.NetworkError:
                    ErrorMessage = _text.GetText(
                        "BillingStoreNetworkError",
                        "Microsoft Store could not be reached. No entitlement changed.");
                    break;
                default:
                    ErrorMessage = _text.GetText(
                        "BillingStoreUnavailableError",
                        "Microsoft Store could not complete the request. No entitlement changed.");
                    break;
            }
        }).ConfigureAwait(true);
    }

    private async Task RestoreAsync()
    {
        if (!CanUseStoreActions())
        {
            return;
        }

        await RunBusyAsync(async cancellationToken =>
        {
            if (!await RefreshAuthorizationAsync(cancellationToken).ConfigureAwait(true))
            {
                return;
            }

            try
            {
                IsStorePurchaseFound = await _storefront
                    .IsSubscriptionInUserCollectionAsync(cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Local collection presence is only a hint; backend reconciliation is authoritative.
                IsStorePurchaseFound = false;
            }

            await ReconcileWithBackendAsync(cancellationToken).ConfigureAwait(true);
            StatusMessage = HasActiveEntitlement
                ? _text.GetText(
                    "BillingRestoreVerifiedStatus",
                    "Purchase verification was refreshed and the MCTX entitlement is active.")
                : _text.GetText(
                    "BillingRestoreNoEntitlementStatus",
                    "No active MCTX entitlement was returned. Microsoft purchase presence alone does not unlock acceleration.");
        }).ConfigureAwait(true);
    }

    public async Task RefreshAsync()
    {
        if (!CanRefresh())
        {
            return;
        }

        await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        await RunBusyAsync(async token =>
        {
            if (!await RefreshAuthorizationAsync(token).ConfigureAwait(true))
            {
                return;
            }

            await LoadEntitlementsAsync(token).ConfigureAwait(true);
            if (IsBackendReady && !IsElevated)
            {
                await LoadProductAsync(token).ConfigureAwait(true);
                if (IsProductAvailable)
                {
                    IsStorePurchaseFound = await _storefront
                        .IsSubscriptionInUserCollectionAsync(token)
                        .ConfigureAwait(true);
                }
            }
        }, cancellationToken).ConfigureAwait(true);
    }

    private async Task<bool> RefreshAuthorizationAsync(CancellationToken cancellationToken)
    {
        ResetAvailability();
        ModPlatformMeta meta;
        try
        {
            meta = await _billingClient.GetMetaAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = _text.GetText(
                "BillingCapabilityUnavailableError",
                "Subscription availability could not be confirmed. Purchasing remains disabled.");
            return false;
        }

        IsCapabilityAvailable = meta.Features.Contains(
            MicrosoftStoreBillingContract.Capability,
            StringComparer.Ordinal);
        if (!IsCapabilityAvailable)
        {
            StatusMessage = _text.GetText(
                "BillingCapabilityUnavailableStatus",
                "Microsoft Store billing is not enabled by the MCTX service. Purchasing is hidden.");
            return false;
        }

        if (!await _credentials.IsConfiguredAsync(cancellationToken).ConfigureAwait(true))
        {
            StatusMessage = _text.GetText(
                "BillingLoginRequiredStatus",
                "Sign in to the LocaleSmith/MCTX account with a PAT before purchasing or restoring.");
            return false;
        }

        try
        {
            var session = await _billingClient
                .GetAuthenticatedSessionAsync(cancellationToken)
                .ConfigureAwait(true);
            _authenticatedUserId = session.User.Id;
            IsAuthenticated = true;
            HasAcceleratedDownloadScope = session.Scopes.Contains(
                "downloads:accelerated",
                StringComparer.Ordinal);
            if (!HasAcceleratedDownloadScope)
            {
                StatusMessage = _text.GetText(
                    "BillingAcceleratedScopeRequiredStatus",
                    "The saved PAT needs the downloads:accelerated scope before billing or acceleration can be used.");
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusMessage = _text.GetText(
                "BillingLoginRequiredStatus",
                "Sign in to the LocaleSmith/MCTX account with a PAT before purchasing or restoring.");
            return false;
        }
    }

    private async Task<bool> LoadProductAsync(CancellationToken cancellationToken)
    {
        try
        {
            var product = await _storefront.GetSubscriptionAsync(cancellationToken).ConfigureAwait(true);
            if (product is null
                || !product.IsMonthly
                || product.StoreId != MicrosoftStoreBillingContract.SubscriptionStoreId
                || product.ProductId != MicrosoftStoreBillingContract.SubscriptionProductId)
            {
                IsProductAvailable = false;
                return false;
            }

            FormattedStorePrice = product.FormattedPrice;
            OffersSevenDayTrial = product.OffersSevenDayTrial;
            IsProductAvailable = true;
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = _text.GetText(
                "BillingStoreUnavailableError",
                "Microsoft Store could not complete the request. No entitlement changed.");
            IsProductAvailable = false;
            return false;
        }
    }

    private async Task LoadEntitlementsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _billingClient.GetEntitlementsAsync(cancellationToken).ConfigureAwait(true);
            HasActiveEntitlement = response.Data.Any(
                entitlement => entitlement.IsUsable(response.ServerTime));
            IsBackendReady = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            HasActiveEntitlement = false;
            IsBackendReady = false;
            ErrorMessage = _text.GetText(
                "BillingBackendUnavailableError",
                "MCTX entitlement status is unavailable. Purchasing remains disabled.");
        }
    }

    private async Task ReconcileWithBackendAsync(CancellationToken cancellationToken)
    {
        if (_authenticatedUserId is not { } userId)
        {
            throw new InvalidOperationException("An authenticated MCTX account is required.");
        }

        using var ticket = await _billingClient
            .RequestMicrosoftStoreServiceTicketAsync(cancellationToken)
            .ConfigureAwait(true);
        if (ticket.ExpiresAt <= _timeProvider.GetUtcNow()
            || ticket.PublisherUserId != userId
            || ticket.ParentStoreId != MicrosoftStoreBillingContract.ParentAppStoreId
            || ticket.SubscriptionStoreId != MicrosoftStoreBillingContract.SubscriptionStoreId)
        {
            throw new InvalidOperationException("The MCTX service ticket does not match the current account or Store product.");
        }

        using var storeIdKey = await _storefront
            .GetCustomerPurchaseIdAsync(
                ticket.Ticket,
                ticket.PublisherUserId.ToString("D"),
                cancellationToken)
            .ConfigureAwait(true);
        await _billingClient
            .VerifyMicrosoftStorePurchaseAsync(storeIdKey, cancellationToken)
            .ConfigureAwait(true);
        await LoadEntitlementsAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task RunBusyAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        NotifyCommandsChanged();
        try
        {
            await operation(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            ErrorMessage = _text.GetText(
                "BillingVerificationFailedError",
                "Purchase verification could not be completed. No entitlement was unlocked; use Restore purchase to retry.");
        }
        finally
        {
            IsBusy = false;
            NotifyCommandsChanged();
            NotifyPresentationChanged();
        }
    }

    private void ResetAvailability()
    {
        IsCapabilityAvailable = false;
        IsAuthenticated = false;
        HasAcceleratedDownloadScope = false;
        IsBackendReady = false;
        IsProductAvailable = false;
        HasActiveEntitlement = false;
        IsStorePurchaseFound = false;
        OffersSevenDayTrial = false;
        FormattedStorePrice = null;
        _authenticatedUserId = null;
    }

    private bool CanPurchase() => !IsBusy && IsPurchaseEntryVisible;

    private bool CanUseStoreActions() => !IsBusy && AreStoreActionsVisible;

    private bool CanRefresh() => !IsBusy;

    private void NotifyCommandsChanged()
    {
        PurchaseCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(ShowCapabilityUnavailable));
        OnPropertyChanged(nameof(ShowLoginRequired));
        OnPropertyChanged(nameof(ShowScopeRequired));
        OnPropertyChanged(nameof(ShowBackendUnavailable));
        OnPropertyChanged(nameof(ShowStoreUnavailable));
        OnPropertyChanged(nameof(ShowActiveEntitlement));
        OnPropertyChanged(nameof(ShowInactiveEntitlement));
        OnPropertyChanged(nameof(IsPurchaseEntryVisible));
        OnPropertyChanged(nameof(AreStoreActionsVisible));
        NotifyCommandsChanged();
    }
}
