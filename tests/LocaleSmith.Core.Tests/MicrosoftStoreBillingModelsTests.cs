using LocaleSmith.Core.Models;

namespace LocaleSmith.Core.Tests;

public sealed class MicrosoftStoreBillingModelsTests
{
    [Fact]
    public void ContractPinsOnlyPublicStoreIdentifiersAndAccountLinks()
    {
        Assert.Equal("microsoft_store_billing_v1", MicrosoftStoreBillingContract.Capability);
        Assert.Equal("9NP8V6WQNGT0", MicrosoftStoreBillingContract.ParentAppStoreId);
        Assert.Equal("9N92NJ4D37P3", MicrosoftStoreBillingContract.SubscriptionStoreId);
        Assert.Equal(
            "localesmith_domestic_acceleration_monthly",
            MicrosoftStoreBillingContract.SubscriptionProductId);
        Assert.Equal(
            "domestic_download_acceleration",
            MicrosoftStoreBillingContract.AccelerationEntitlementKey);
        Assert.Equal(
            "8ae5095a-006a-4561-a7ab-8ee6dc5728ba",
            MicrosoftStoreBillingContract.EntraClientId);
        Assert.Equal(
            "03143e3a-2be2-4b6a-829c-1b548beb8a9d",
            MicrosoftStoreBillingContract.EntraTenantId);
        Assert.NotEqual(
            MicrosoftStoreBillingContract.SubscriptionStoreId,
            MicrosoftStoreBillingContract.SubscriptionProductId);
        Assert.NotEqual(
            MicrosoftStoreBillingContract.SubscriptionProductId,
            MicrosoftStoreBillingContract.AccelerationEntitlementKey);
        Assert.Equal(
            "https://account.microsoft.com/services",
            MicrosoftStoreBillingContract.ManageSubscriptionsUri.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(
            "https://dow.dzxh-tx.cn/privacy",
            MicrosoftStoreBillingContract.PrivacyPolicyUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("active", "microsoft_store", "9N92NJ4D37P3", true)]
    [InlineData("expired", "microsoft_store", "9N92NJ4D37P3", false)]
    [InlineData("active", "manual", "9N92NJ4D37P3", false)]
    [InlineData("active", "microsoft_store", "different", false)]
    public void EntitlementRequiresExactBackendAuthority(
        string status,
        string provider,
        string sourceProductId,
        bool expected)
    {
        var now = DateTimeOffset.Parse(
            "2026-08-24T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var entitlement = CreateEntitlement(
            status: status,
            provider: provider,
            sourceProductId: sourceProductId,
            validFrom: now.AddDays(-1),
            validUntil: now.AddDays(1),
            lastVerifiedAt: now.AddMinutes(-5));

        Assert.Equal(expected, entitlement.IsUsable(now));
    }

    [Fact]
    public void EntitlementRejectsFutureExpiredAndFutureVerificationTimestamps()
    {
        var now = DateTimeOffset.Parse(
            "2026-08-24T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.False(CreateEntitlement(validFrom: now.AddMinutes(1)).IsUsable(now));
        Assert.False(CreateEntitlement(validUntil: now).IsUsable(now));
        Assert.False(CreateEntitlement(lastVerifiedAt: now.AddMinutes(1)).IsUsable(now));
    }

    [Fact]
    public void ServiceTicketDisposesSecretMaterial()
    {
        var secret = new SecretValue("secret-ticket-value-with-enough-entropy".AsSpan());
        var ticket = new MicrosoftStoreServiceTicket(
            secret,
            DateTimeOffset.UtcNow.AddMinutes(5),
            Guid.NewGuid(),
            MicrosoftStoreBillingContract.ParentAppStoreId,
            MicrosoftStoreBillingContract.SubscriptionStoreId);

        ticket.Dispose();

        Assert.Throws<ObjectDisposedException>(() => secret.DangerousGetString());
    }

    private static MicrosoftStoreEntitlement CreateEntitlement(
        string status = "active",
        string provider = "microsoft_store",
        string sourceProductId = MicrosoftStoreBillingContract.SubscriptionStoreId,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        DateTimeOffset? lastVerifiedAt = null)
    {
        var now = DateTimeOffset.Parse(
            "2026-08-24T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        return new MicrosoftStoreEntitlement(
            Guid.NewGuid(),
            MicrosoftStoreBillingContract.AccelerationEntitlementKey,
            status,
            validFrom ?? now.AddDays(-1),
            validUntil ?? now.AddDays(1),
            lastVerifiedAt ?? now.AddMinutes(-5),
            provider,
            sourceProductId,
            "monthly-sku",
            status == "active" ? "active" : "expired");
    }
}
