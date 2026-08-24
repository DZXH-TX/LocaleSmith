using System.Text.Json.Serialization;

namespace LocaleSmith.Core.Models;

public static class MicrosoftStoreBillingContract
{
    public const string Capability = "microsoft_store_billing_v1";
    public const string AcceleratedDownloadsCapability = "accelerated_downloads_v1";
    public const string ParentAppStoreId = "9NP8V6WQNGT0";
    public const string SubscriptionStoreId = "9N92NJ4D37P3";
    public const string SubscriptionProductId = "localesmith_domestic_acceleration_monthly";
    public const string AccelerationEntitlementKey = "domestic_download_acceleration";
    public const string EntraClientId = "8ae5095a-006a-4561-a7ab-8ee6dc5728ba";
    public const string EntraTenantId = "03143e3a-2be2-4b6a-829c-1b548beb8a9d";
    public const string ProductKind = "Durable";

    public static Uri ManageSubscriptionsUri { get; } = new("https://account.microsoft.com/services");

    public static Uri PrivacyPolicyUri { get; } = new("https://dow.dzxh-tx.cn/privacy");
}

public sealed class MicrosoftStoreServiceTicket : IDisposable
{
    public MicrosoftStoreServiceTicket(
        SecretValue ticket,
        DateTimeOffset expiresAt,
        Guid publisherUserId,
        string parentStoreId,
        string subscriptionStoreId)
    {
        Ticket = ticket ?? throw new ArgumentNullException(nameof(ticket));
        ExpiresAt = expiresAt;
        PublisherUserId = publisherUserId;
        ParentStoreId = parentStoreId;
        SubscriptionStoreId = subscriptionStoreId;
    }

    public SecretValue Ticket { get; }

    public DateTimeOffset ExpiresAt { get; }

    public Guid PublisherUserId { get; }

    public string ParentStoreId { get; }

    public string SubscriptionStoreId { get; }

    public void Dispose() => Ticket.Dispose();
}

public sealed record MicrosoftStoreProductInfo(
    string StoreId,
    string ProductId,
    string Title,
    string FormattedPrice,
    bool IsMonthly,
    bool OffersSevenDayTrial);

public enum MicrosoftStorePurchaseStatus
{
    Succeeded,
    AlreadyPurchased,
    NotPurchased,
    NetworkError,
    ServerError,
    Unavailable
}

public sealed record MicrosoftStorePurchaseOutcome(MicrosoftStorePurchaseStatus Status);

public sealed record MicrosoftStoreEntitlement(
    Guid Id,
    [property: JsonPropertyName("entitlement_key")] string EntitlementKey,
    string Status,
    [property: JsonPropertyName("valid_from")] DateTimeOffset ValidFrom,
    [property: JsonPropertyName("valid_until")] DateTimeOffset ValidUntil,
    [property: JsonPropertyName("last_verified_at")] DateTimeOffset LastVerifiedAt,
    [property: JsonPropertyName("source_provider")] string SourceProvider,
    [property: JsonPropertyName("source_product_id")] string SourceProductId,
    [property: JsonPropertyName("source_sku_id")] string SourceSkuId,
    [property: JsonPropertyName("source_status")] string SourceStatus)
{
    public bool IsUsable(DateTimeOffset now) =>
        Id != Guid.Empty
        && EntitlementKey == MicrosoftStoreBillingContract.AccelerationEntitlementKey
        && SourceProvider == "microsoft_store"
        && SourceProductId == MicrosoftStoreBillingContract.SubscriptionStoreId
        && Status == "active"
        && ValidFrom <= now
        && ValidUntil > now
        && LastVerifiedAt <= now
        && SourceStatus is "active" or "grace_period";
}

public sealed record MicrosoftStoreEntitlements(
    IReadOnlyList<MicrosoftStoreEntitlement> Data,
    [property: JsonPropertyName("server_time")] DateTimeOffset ServerTime);

public sealed record MicrosoftStoreMeta(
    [property: JsonPropertyName("catalog_status")] string CatalogStatus,
    [property: JsonPropertyName("parent_store_id")] string ParentStoreId,
    [property: JsonPropertyName("subscription_store_id")] string SubscriptionStoreId,
    [property: JsonPropertyName("internal_product_id")] string InternalProductId,
    [property: JsonPropertyName("billing_period")] string BillingPeriod,
    [property: JsonPropertyName("trial_days")] int TrialDays,
    MicrosoftStorePricingMeta Pricing,
    [property: JsonPropertyName("hidden_parent_app_only")] bool HiddenParentAppOnly,
    [property: JsonPropertyName("privacy_url")] string PrivacyUrl);

public sealed record MicrosoftStorePricingMeta(
    [property: JsonPropertyName("base_currency")] string BaseCurrency,
    [property: JsonPropertyName("base_amount")] string BaseAmount,
    [property: JsonPropertyName("localized_by_store")] bool LocalizedByStore,
    [property: JsonPropertyName("china_currency")] string ChinaCurrency,
    [property: JsonPropertyName("china_amount")] string ChinaAmount,
    [property: JsonPropertyName("introductory_price")] string? IntroductoryPrice);

public sealed record ModPlatformDownloadSource(
    string Id,
    string Kind,
    [property: JsonPropertyName("download_url")] string DownloadUrl,
    [property: JsonPropertyName("supports_range")] bool SupportsRange);

public sealed record ModPlatformAdditionalDownloadSource(
    string Status,
    [property: JsonPropertyName("reason_code")] string? ReasonCode,
    [property: JsonPropertyName("grant_url")] string? GrantUrl,
    [property: JsonPropertyName("browser_parallel_range_enabled")] bool? BrowserParallelRangeEnabled = null);

public sealed record ModPlatformDownloadSources(
    [property: JsonPropertyName("version_id")] Guid VersionId,
    string Filename,
    long Size,
    string Sha256,
    IReadOnlyList<ModPlatformDownloadSource> Sources,
    [property: JsonPropertyName("additional_source")] ModPlatformAdditionalDownloadSource AdditionalSource)
{
    public bool IsAccelerationAvailable =>
        AdditionalSource.Status == "available"
        && AdditionalSource.ReasonCode is null
        && AdditionalSource.GrantUrl is not null;
}

public sealed class ModPlatformAcceleratedDownloadGrant : IDisposable
{
    private char[]? _getUrl;
    private char[]? _headUrl;

    public ModPlatformAcceleratedDownloadGrant(
        Guid grantId,
        Guid versionId,
        ReadOnlySpan<char> getUrl,
        ReadOnlySpan<char> headUrl,
        DateTimeOffset expiresAt,
        string fallbackUrl,
        long size,
        string sha256,
        bool supportsRange,
        bool browserParallelRangeEnabled)
    {
        GrantId = grantId;
        VersionId = versionId;
        _getUrl = getUrl.ToArray();
        _headUrl = headUrl.ToArray();
        ExpiresAt = expiresAt;
        FallbackUrl = fallbackUrl;
        Size = size;
        Sha256 = sha256;
        SupportsRange = supportsRange;
        BrowserParallelRangeEnabled = browserParallelRangeEnabled;
    }

    public Guid GrantId { get; }

    public Guid VersionId { get; }

    public DateTimeOffset ExpiresAt { get; }

    public string FallbackUrl { get; }

    public long Size { get; }

    public string Sha256 { get; }

    public bool SupportsRange { get; }

    public bool BrowserParallelRangeEnabled { get; }

    public string DangerousGetUrl() => new(GetUrlCharacters());

    public string DangerousGetHeadUrl() => new(GetHeadUrlCharacters());

    public void Dispose()
    {
        ZeroAndRelease(ref _getUrl);
        ZeroAndRelease(ref _headUrl);
    }

    public override string ToString() =>
        $"ModPlatformAcceleratedDownloadGrant {{ GrantId = {GrantId}, VersionId = {VersionId}, [URLs redacted] }}";

    private ReadOnlySpan<char> GetUrlCharacters()
    {
        ObjectDisposedException.ThrowIf(_getUrl is null, this);
        return _getUrl;
    }

    private ReadOnlySpan<char> GetHeadUrlCharacters()
    {
        ObjectDisposedException.ThrowIf(_headUrl is null, this);
        return _headUrl;
    }

    private static void ZeroAndRelease(ref char[]? value)
    {
        var characters = Interlocked.Exchange(ref value, null);
        if (characters is not null)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }
}

public enum ModPlatformDownloadRoute
{
    Default,
    DomesticAcceleration
}

public sealed record ModPlatformAccelerationAvailability(
    bool IsAvailable,
    string? ReasonCode,
    bool ParallelRangeEnabled)
{
    public static ModPlatformAccelerationAvailability Unavailable(string reasonCode) =>
        new(false, reasonCode, false);
}

public sealed record ModPlatformArtifactDownloadResult(
    ModPlatformDownloadRoute RequestedRoute,
    ModPlatformDownloadRoute ActualRoute,
    bool FellBack,
    string? SafeReasonCode);
