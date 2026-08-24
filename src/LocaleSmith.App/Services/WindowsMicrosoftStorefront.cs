using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using Microsoft.UI.Xaml;
using Windows.Services.Store;
using WinRT.Interop;

namespace LocaleSmith.App.Services;

/// <summary>
/// Owns the Windows.Services.Store context for the current desktop window. Store results are only
/// purchase evidence; backend entitlements remain the sole authority for accelerated access.
/// </summary>
internal sealed class WindowsMicrosoftStorefront : IMicrosoftStorefront, IDisposable
{
    private readonly Func<Window?> _windowAccessor;
    private StoreContext? _context;
    private nint _ownerWindow;
    private bool _disposed;

    internal WindowsMicrosoftStorefront(Func<Window?> windowAccessor)
    {
        _windowAccessor = windowAccessor ?? throw new ArgumentNullException(nameof(windowAccessor));
    }

    public async Task<MicrosoftStoreProductInfo?> GetSubscriptionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = GetContext();
        var result = await context.GetAssociatedStoreProductsAsync(
            [MicrosoftStoreBillingContract.ProductKind]);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSuccessful(result.ExtendedError);

        var product = result.Products.Values.FirstOrDefault(static candidate =>
            string.Equals(
                candidate.StoreId,
                MicrosoftStoreBillingContract.SubscriptionStoreId,
                StringComparison.Ordinal)
            && string.Equals(
                candidate.InAppOfferToken,
                MicrosoftStoreBillingContract.SubscriptionProductId,
                StringComparison.Ordinal)
            && string.Equals(
                candidate.ProductKind,
                MicrosoftStoreBillingContract.ProductKind,
                StringComparison.Ordinal));
        if (product is null)
        {
            return null;
        }

        return MapSubscriptionProduct(
            product.StoreId,
            product.InAppOfferToken,
            product.Title,
            product.ProductKind,
            product.Price.FormattedRecurrencePrice,
            product.Skus.Select(static sku => new SubscriptionSkuSnapshot(
                sku.IsSubscription,
                sku.SubscriptionInfo?.BillingPeriod ?? 0,
                sku.SubscriptionInfo?.BillingPeriodUnit.ToString() ?? string.Empty,
                sku.SubscriptionInfo?.HasTrialPeriod == true,
                sku.SubscriptionInfo?.TrialPeriod ?? 0,
                sku.SubscriptionInfo?.TrialPeriodUnit.ToString() ?? string.Empty,
                sku.Price.FormattedRecurrencePrice)));
    }

    public async Task<MicrosoftStorePurchaseOutcome> RequestSubscriptionPurchaseAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await GetContext().RequestPurchaseAsync(
            MicrosoftStoreBillingContract.SubscriptionStoreId);
        cancellationToken.ThrowIfCancellationRequested();
        return new MicrosoftStorePurchaseOutcome(MapPurchaseStatus(result.Status));
    }

    internal static MicrosoftStoreProductInfo? MapSubscriptionProduct(
        string storeId,
        string productId,
        string title,
        string productKind,
        string fallbackFormattedPrice,
        IEnumerable<SubscriptionSkuSnapshot> skus)
    {
        ArgumentNullException.ThrowIfNull(skus);
        if (storeId != MicrosoftStoreBillingContract.SubscriptionStoreId
            || productId != MicrosoftStoreBillingContract.SubscriptionProductId
            || productKind != MicrosoftStoreBillingContract.ProductKind)
        {
            return null;
        }

        var subscription = skus.FirstOrDefault(static sku =>
            sku.IsSubscription && sku.BillingPeriod == 1 && sku.BillingUnit == "Month");
        if (subscription is null)
        {
            return null;
        }

        var formattedPrice = string.IsNullOrWhiteSpace(subscription.FormattedRecurrencePrice)
            ? fallbackFormattedPrice
            : subscription.FormattedRecurrencePrice;
        if (string.IsNullOrWhiteSpace(formattedPrice))
        {
            return null;
        }

        var offersSevenDayTrial = subscription.HasTrial
            && ((subscription.TrialPeriod == 1 && subscription.TrialUnit == "Week")
                || (subscription.TrialPeriod == 7 && subscription.TrialUnit == "Day"));
        return new MicrosoftStoreProductInfo(
            storeId,
            productId,
            title,
            formattedPrice,
            IsMonthly: true,
            OffersSevenDayTrial: offersSevenDayTrial);
    }

    internal static MicrosoftStorePurchaseStatus MapPurchaseStatus(StorePurchaseStatus status) => status switch
    {
        StorePurchaseStatus.Succeeded => MicrosoftStorePurchaseStatus.Succeeded,
        StorePurchaseStatus.AlreadyPurchased => MicrosoftStorePurchaseStatus.AlreadyPurchased,
        StorePurchaseStatus.NotPurchased => MicrosoftStorePurchaseStatus.NotPurchased,
        StorePurchaseStatus.NetworkError => MicrosoftStorePurchaseStatus.NetworkError,
        StorePurchaseStatus.ServerError => MicrosoftStorePurchaseStatus.ServerError,
        _ => MicrosoftStorePurchaseStatus.Unavailable
    };

    public async Task<bool> IsSubscriptionInUserCollectionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await GetContext().GetUserCollectionAsync(
            [MicrosoftStoreBillingContract.ProductKind]);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSuccessful(result.ExtendedError);
        return result.Products.Values.Any(static candidate =>
            string.Equals(
                candidate.StoreId,
                MicrosoftStoreBillingContract.SubscriptionStoreId,
                StringComparison.Ordinal)
            && string.Equals(
                candidate.InAppOfferToken,
                MicrosoftStoreBillingContract.SubscriptionProductId,
                StringComparison.Ordinal));
    }

    public async Task<SecretValue> GetCustomerPurchaseIdAsync(
        SecretValue serviceTicket,
        string publisherUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceTicket);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherUserId);
        cancellationToken.ThrowIfCancellationRequested();

        var key = await GetContext().GetCustomerPurchaseIdAsync(
            serviceTicket.DangerousGetString(),
            publisherUserId);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(key)
            || key.Length > 32768
            || key.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new InvalidOperationException("Microsoft Store did not return a valid purchase ID key.");
        }

        return new SecretValue(key.AsSpan());
    }

    public void Dispose()
    {
        _disposed = true;
        _context = null;
        _ownerWindow = 0;
    }

    private StoreContext GetContext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var window = _windowAccessor()
            ?? throw new InvalidOperationException("The main window is not available for Microsoft Store UI.");
        var windowHandle = WindowNative.GetWindowHandle(window);
        if (windowHandle == 0)
        {
            throw new InvalidOperationException("The main window handle is unavailable for Microsoft Store UI.");
        }

        if (_context is not null && _ownerWindow == windowHandle)
        {
            return _context;
        }

        var context = StoreContext.GetDefault();
        InitializeWithWindow.Initialize(context, windowHandle);
        _context = context;
        _ownerWindow = windowHandle;
        return context;
    }

    private static void EnsureSuccessful(Exception? extendedError)
    {
        if (extendedError is not null)
        {
            throw new InvalidOperationException("Microsoft Store is unavailable for the current Windows user.");
        }
    }

    internal sealed record SubscriptionSkuSnapshot(
        bool IsSubscription,
        uint BillingPeriod,
        string BillingUnit,
        bool HasTrial,
        uint TrialPeriod,
        string TrialUnit,
        string FormattedRecurrencePrice);
}
