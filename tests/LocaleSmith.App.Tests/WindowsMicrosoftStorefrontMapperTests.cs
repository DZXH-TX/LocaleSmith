using LocaleSmith.App.Services;
using LocaleSmith.Core.Models;
using Windows.Services.Store;

namespace LocaleSmith.App.Tests;

public sealed class WindowsMicrosoftStorefrontMapperTests
{
    [Theory]
    [InlineData(1u, "Week", true)]
    [InlineData(7u, "Day", true)]
    [InlineData(0u, "Minute", false)]
    public void ExactMonthlySubscriptionMapsLocalizedRecurrenceAndTrialEligibility(
        uint trialPeriod,
        string trialUnit,
        bool expectedTrial)
    {
        var product = WindowsMicrosoftStorefront.MapSubscriptionProduct(
            MicrosoftStoreBillingContract.SubscriptionStoreId,
            MicrosoftStoreBillingContract.SubscriptionProductId,
            "Domestic acceleration",
            MicrosoftStoreBillingContract.ProductKind,
            "fallback price",
            [new WindowsMicrosoftStorefront.SubscriptionSkuSnapshot(
                true,
                1,
                "Month",
                trialPeriod > 0,
                trialPeriod,
                trialUnit,
                "CNY 30.00/month")]);

        Assert.NotNull(product);
        Assert.True(product.IsMonthly);
        Assert.Equal(expectedTrial, product.OffersSevenDayTrial);
        Assert.Equal("CNY 30.00/month", product.FormattedPrice);
    }

    [Theory]
    [InlineData(false, 1u, "Month")]
    [InlineData(true, 3u, "Month")]
    [InlineData(true, 1u, "Year")]
    public void DurableNonMonthlySkuFailsClosed(bool isSubscription, uint period, string unit)
    {
        var product = WindowsMicrosoftStorefront.MapSubscriptionProduct(
            MicrosoftStoreBillingContract.SubscriptionStoreId,
            MicrosoftStoreBillingContract.SubscriptionProductId,
            "Domestic acceleration",
            MicrosoftStoreBillingContract.ProductKind,
            "fallback price",
            [new WindowsMicrosoftStorefront.SubscriptionSkuSnapshot(
                isSubscription,
                period,
                unit,
                true,
                1,
                "Week",
                "CNY 30.00/month")]);

        Assert.Null(product);
    }

    [Fact]
    public void StoreAndInternalProductIdentifiersMustBothMatch()
    {
        var sku = new WindowsMicrosoftStorefront.SubscriptionSkuSnapshot(
            true,
            1,
            "Month",
            true,
            1,
            "Week",
            "CNY 30.00/month");

        Assert.Null(WindowsMicrosoftStorefront.MapSubscriptionProduct(
            "wrong",
            MicrosoftStoreBillingContract.SubscriptionProductId,
            "Title",
            MicrosoftStoreBillingContract.ProductKind,
            string.Empty,
            [sku]));
        Assert.Null(WindowsMicrosoftStorefront.MapSubscriptionProduct(
            MicrosoftStoreBillingContract.SubscriptionStoreId,
            "wrong",
            "Title",
            MicrosoftStoreBillingContract.ProductKind,
            string.Empty,
            [sku]));
    }

    [Theory]
    [InlineData(StorePurchaseStatus.Succeeded, MicrosoftStorePurchaseStatus.Succeeded)]
    [InlineData(StorePurchaseStatus.AlreadyPurchased, MicrosoftStorePurchaseStatus.AlreadyPurchased)]
    [InlineData(StorePurchaseStatus.NotPurchased, MicrosoftStorePurchaseStatus.NotPurchased)]
    [InlineData(StorePurchaseStatus.NetworkError, MicrosoftStorePurchaseStatus.NetworkError)]
    [InlineData(StorePurchaseStatus.ServerError, MicrosoftStorePurchaseStatus.ServerError)]
    public void PurchaseStatusMappingIsStable(
        StorePurchaseStatus storeStatus,
        MicrosoftStorePurchaseStatus expected) =>
        Assert.Equal(expected, WindowsMicrosoftStorefront.MapPurchaseStatus(storeStatus));
}
