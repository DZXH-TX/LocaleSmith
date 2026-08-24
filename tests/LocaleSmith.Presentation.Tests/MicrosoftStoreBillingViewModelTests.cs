using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.Presentation.Tests;

public sealed class MicrosoftStoreBillingViewModelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-08-24T12:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task MissingCapabilityFailsClosedBeforeCredentialsOrStore()
    {
        var billing = new FakeBillingClient { Meta = CreateMeta(includeBilling: false) };
        var credentials = new FakeCredentialService { IsConfigured = true };
        var store = new FakeStorefront();
        var viewModel = CreateViewModel(billing, credentials, store);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.ShowCapabilityUnavailable);
        Assert.False(viewModel.IsPurchaseEntryVisible);
        Assert.Equal(0, credentials.ConfigurationChecks);
        Assert.Equal(0, store.TotalCalls);
    }

    [Fact]
    public async Task MissingPatFailsClosedBeforeAuthenticatedEndpointsOrStore()
    {
        var billing = new FakeBillingClient();
        var credentials = new FakeCredentialService();
        var store = new FakeStorefront();
        var viewModel = CreateViewModel(billing, credentials, store);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.ShowLoginRequired);
        Assert.Equal(0, billing.SessionCalls);
        Assert.Equal(0, billing.EntitlementCalls);
        Assert.Equal(0, store.TotalCalls);
    }

    [Fact]
    public async Task PatWithoutAcceleratedScopeDisablesBillingAndStore()
    {
        var billing = new FakeBillingClient
        {
            Session = CreateSession(["profile:read"])
        };
        var store = new FakeStorefront();
        var viewModel = CreateViewModel(
            billing,
            new FakeCredentialService { IsConfigured = true },
            store);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsAuthenticated);
        Assert.True(viewModel.ShowScopeRequired);
        Assert.False(viewModel.IsPurchaseEntryVisible);
        Assert.Equal(0, billing.EntitlementCalls);
        Assert.Equal(0, store.TotalCalls);
    }

    [Fact]
    public async Task BackendEntitlementFailurePreventsStoreCalls()
    {
        var billing = new FakeBillingClient
        {
            EntitlementError = new InvalidOperationException("secret backend detail")
        };
        var store = new FakeStorefront();
        var viewModel = CreateViewModel(
            billing,
            new FakeCredentialService { IsConfigured = true },
            store);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.ShowBackendUnavailable);
        Assert.False(viewModel.IsPurchaseEntryVisible);
        Assert.DoesNotContain("secret backend detail", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, store.TotalCalls);
    }

    [Fact]
    public async Task StoreCollectionPresenceNeverUnlocksEntitlement()
    {
        var billing = new FakeBillingClient();
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: false));
        var store = new FakeStorefront { InUserCollection = true };
        var viewModel = CreateViewModel(
            billing,
            new FakeCredentialService { IsConfigured = true },
            store);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsStorePurchaseFound);
        Assert.False(viewModel.HasActiveEntitlement);
        Assert.True(viewModel.IsPurchaseEntryVisible);
    }

    [Theory]
    [InlineData(MicrosoftStorePurchaseStatus.Succeeded)]
    [InlineData(MicrosoftStorePurchaseStatus.AlreadyPurchased)]
    public async Task SuccessfulStoreResultUnlocksOnlyAfterBackendEntitlement(
        MicrosoftStorePurchaseStatus purchaseStatus)
    {
        var events = new List<string>();
        var billing = new FakeBillingClient(events);
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: false));
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: false));
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: true));
        var store = new FakeStorefront(events) { PurchaseStatus = purchaseStatus };
        var viewModel = CreateViewModel(
            billing,
            new FakeCredentialService { IsConfigured = true },
            store);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.PurchaseCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasActiveEntitlement);
        Assert.Equal(1, store.PurchaseCalls);
        Assert.Equal(1, billing.VerifyCalls);
        Assert.Equal(
            ["ticket", "purchase-key", "verify", "entitlements"],
            events.TakeLast(4));
        Assert.NotNull(billing.LastVerifiedKey);
        Assert.Throws<ObjectDisposedException>(() => billing.LastVerifiedKey.DangerousGetString());
    }

    [Fact]
    public async Task CancelledPurchaseDoesNotVerifyOrUnlock()
    {
        var billing = new FakeBillingClient();
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: false));
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: false));
        var store = new FakeStorefront
        {
            PurchaseStatus = MicrosoftStorePurchaseStatus.NotPurchased
        };
        var viewModel = CreateViewModel(
            billing,
            new FakeCredentialService { IsConfigured = true },
            store);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.PurchaseCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasActiveEntitlement);
        Assert.Equal(0, billing.VerifyCalls);
        Assert.Equal(0, billing.TicketCalls);
    }

    [Fact]
    public async Task PurchaseRechecksBackendAndSkipsStoreWhenEntitlementBecameActive()
    {
        var billing = new FakeBillingClient();
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: false));
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: true));
        var store = new FakeStorefront();
        var viewModel = CreateViewModel(
            billing,
            new FakeCredentialService { IsConfigured = true },
            store);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.PurchaseCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasActiveEntitlement);
        Assert.Equal(0, store.PurchaseCalls);
        Assert.Equal(0, billing.VerifyCalls);
    }

    [Fact]
    public async Task RestoreUsesBackendReconciliationWithoutOpeningPurchaseUi()
    {
        var events = new List<string>();
        var billing = new FakeBillingClient(events);
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: false));
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: true));
        var store = new FakeStorefront(events) { InUserCollection = true };
        var viewModel = CreateViewModel(
            billing,
            new FakeCredentialService { IsConfigured = true },
            store);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.RestoreCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasActiveEntitlement);
        Assert.Equal(0, store.PurchaseCalls);
        Assert.Equal(1, billing.VerifyCalls);
    }

    [Fact]
    public async Task RestoreStillReconcilesWhenLocalCollectionHintFails()
    {
        var billing = new FakeBillingClient();
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: false));
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: true));
        var store = new FakeStorefront
        {
            CollectionError = new InvalidOperationException("local Store hint failed")
        };
        var viewModel = CreateViewModel(
            billing,
            new FakeCredentialService { IsConfigured = true },
            store);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.RestoreCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasActiveEntitlement);
        Assert.Equal(1, billing.VerifyCalls);
        Assert.Equal(0, store.PurchaseCalls);
    }

    [Fact]
    public async Task MismatchedServiceTicketNeverVerifiesOrUnlocks()
    {
        var billing = new FakeBillingClient
        {
            TicketFactory = () => new MicrosoftStoreServiceTicket(
                new SecretValue("service-ticket-value-with-enough-entropy".AsSpan()),
                Now.AddMinutes(5),
                Guid.NewGuid(),
                MicrosoftStoreBillingContract.ParentAppStoreId,
                MicrosoftStoreBillingContract.SubscriptionStoreId)
        };
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: false));
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: false));
        var viewModel = CreateViewModel(
            billing,
            new FakeCredentialService { IsConfigured = true },
            new FakeStorefront());
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.PurchaseCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasActiveEntitlement);
        Assert.Equal(0, billing.VerifyCalls);
        Assert.True(viewModel.HasError);
    }

    [Fact]
    public async Task UnrelatedHistoricalEntitlementsDoNotHideValidTarget()
    {
        var billing = new FakeBillingClient();
        var valid = Assert.Single(CreateEntitlements(active: true).Data);
        billing.EntitlementResponses.Enqueue(new MicrosoftStoreEntitlements(
            [
                new MicrosoftStoreEntitlement(
                    Guid.NewGuid(),
                    "future_partner_benefit",
                    "future_status",
                    Now.AddDays(-10),
                    Now.AddDays(10),
                    Now.AddDays(-2),
                    "future_provider",
                    "future.product",
                    "future.sku",
                    "future_state"),
                valid
            ],
            Now));
        var viewModel = CreateViewModel(
            billing,
            new FakeCredentialService { IsConfigured = true },
            new FakeStorefront());

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.HasActiveEntitlement);
        Assert.False(viewModel.IsPurchaseEntryVisible);
    }

    [Fact]
    public async Task ElevatedProcessNeverQueriesOrOpensStore()
    {
        var billing = new FakeBillingClient();
        billing.EntitlementResponses.Enqueue(CreateEntitlements(active: false));
        var store = new FakeStorefront();
        var viewModel = CreateViewModel(
            billing,
            new FakeCredentialService { IsConfigured = true },
            store,
            elevated: true);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.IsPurchaseEntryVisible);
        Assert.False(viewModel.AreStoreActionsVisible);
        Assert.Equal(0, store.TotalCalls);
    }

    private static MicrosoftStoreBillingViewModel CreateViewModel(
        FakeBillingClient billing,
        FakeCredentialService credentials,
        FakeStorefront store,
        bool elevated = false) => new(
            billing,
            credentials,
            store,
            new FakePrivilegeContext(elevated),
            timeProvider: new FixedTimeProvider(Now));

    private static ModPlatformMeta CreateMeta(bool includeBilling = true)
    {
        var features = new List<string> { "personal_access_token_v1", "forum_v1" };
        if (includeBilling)
        {
            features.Add(MicrosoftStoreBillingContract.Capability);
        }

        return new ModPlatformMeta(
            "MCTX Mod Hub",
            "test",
            [1],
            1,
            features,
            new ModPlatformLimits(2_147_483_648, 8_388_608, 3, 4),
            new ModPlatformTurnstile(false, null),
            Now);
    }

    private static ModPlatformAuthSession CreateSession(IReadOnlyList<string>? scopes = null) => new(
        new ModPlatformUser(Guid.Parse("10000000-0000-0000-0000-000000000001"), "alex", "user"),
        Now.AddDays(30),
        scopes ?? ["profile:read", "downloads:accelerated"]);

    private static MicrosoftStoreEntitlements CreateEntitlements(bool active)
    {
        var data = active
            ? new[]
            {
                new MicrosoftStoreEntitlement(
                    Guid.Parse("20000000-0000-0000-0000-000000000001"),
                    MicrosoftStoreBillingContract.AccelerationEntitlementKey,
                    "active",
                    Now.AddDays(-1),
                    Now.AddDays(29),
                    Now.AddMinutes(-5),
                    "microsoft_store",
                    MicrosoftStoreBillingContract.SubscriptionStoreId,
                    "monthly-sku",
                    "active")
            }
            : [];
        return new MicrosoftStoreEntitlements(data, Now);
    }

    private sealed class FakeBillingClient(List<string>? events = null) : IModPlatformBillingClient
    {
        public ModPlatformMeta Meta { get; set; } = CreateMeta();

        public ModPlatformAuthSession Session { get; set; } = CreateSession();

        public Queue<MicrosoftStoreEntitlements> EntitlementResponses { get; } = new();

        public Exception? EntitlementError { get; set; }

        public int SessionCalls { get; private set; }

        public int EntitlementCalls { get; private set; }

        public int TicketCalls { get; private set; }

        public int VerifyCalls { get; private set; }

        public SecretValue? LastVerifiedKey { get; private set; }

        public Func<MicrosoftStoreServiceTicket>? TicketFactory { get; set; }

        public Exception? VerifyError { get; set; }

        public Task<ModPlatformMeta> GetMetaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Meta);

        public Task<ModPlatformAuthSession> GetAuthenticatedSessionAsync(
            CancellationToken cancellationToken = default)
        {
            SessionCalls++;
            return Task.FromResult(Session);
        }

        public Task<MicrosoftStoreServiceTicket> RequestMicrosoftStoreServiceTicketAsync(
            CancellationToken cancellationToken = default)
        {
            TicketCalls++;
            events?.Add("ticket");
            return Task.FromResult(TicketFactory?.Invoke() ?? new MicrosoftStoreServiceTicket(
                new SecretValue("service-ticket-value-with-enough-entropy".AsSpan()),
                Now.AddMinutes(5),
                Session.User.Id,
                MicrosoftStoreBillingContract.ParentAppStoreId,
                MicrosoftStoreBillingContract.SubscriptionStoreId));
        }

        public Task VerifyMicrosoftStorePurchaseAsync(
            SecretValue storeIdKey,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("store-purchase-key-with-enough-entropy", storeIdKey.DangerousGetString());
            LastVerifiedKey = storeIdKey;
            VerifyCalls++;
            events?.Add("verify");
            return VerifyError is null ? Task.CompletedTask : Task.FromException(VerifyError);
        }

        public Task<MicrosoftStoreEntitlements> GetEntitlementsAsync(
            CancellationToken cancellationToken = default)
        {
            EntitlementCalls++;
            events?.Add("entitlements");
            if (EntitlementError is not null)
            {
                return Task.FromException<MicrosoftStoreEntitlements>(EntitlementError);
            }

            return Task.FromResult(
                EntitlementResponses.Count > 0
                    ? EntitlementResponses.Dequeue()
                    : CreateEntitlements(active: false));
        }

        public Task<ModPlatformDownloadSources> GetDownloadSourcesAsync(
            Guid versionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ModPlatformAcceleratedDownloadGrant> CreateAcceleratedDownloadGrantAsync(
            Guid versionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeCredentialService : IModPlatformCredentialService
    {
        public bool IsConfigured { get; set; }

        public int ConfigurationChecks { get; private set; }

        public ValueTask<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
        {
            ConfigurationChecks++;
            return ValueTask.FromResult(IsConfigured);
        }

        public ValueTask SaveAsync(
            ReadOnlyMemory<char> token,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<bool> DeleteAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class FakeStorefront(List<string>? events = null) : IMicrosoftStorefront
    {
        public bool InUserCollection { get; set; }

        public MicrosoftStorePurchaseStatus PurchaseStatus { get; set; } =
            MicrosoftStorePurchaseStatus.Succeeded;

        public Exception? CollectionError { get; set; }

        public int ProductCalls { get; private set; }

        public int PurchaseCalls { get; private set; }

        public int CollectionCalls { get; private set; }

        public int CollectionsKeyCalls { get; private set; }

        public int TotalCalls => ProductCalls + PurchaseCalls + CollectionCalls + CollectionsKeyCalls;

        public Task<MicrosoftStoreProductInfo?> GetSubscriptionAsync(
            CancellationToken cancellationToken = default)
        {
            ProductCalls++;
            return Task.FromResult<MicrosoftStoreProductInfo?>(new(
                MicrosoftStoreBillingContract.SubscriptionStoreId,
                MicrosoftStoreBillingContract.SubscriptionProductId,
                "LocaleSmith domestic acceleration",
                "CNY 30.00/month",
                IsMonthly: true,
                OffersSevenDayTrial: true));
        }

        public Task<MicrosoftStorePurchaseOutcome> RequestSubscriptionPurchaseAsync(
            CancellationToken cancellationToken = default)
        {
            PurchaseCalls++;
            return Task.FromResult(new MicrosoftStorePurchaseOutcome(PurchaseStatus));
        }

        public Task<bool> IsSubscriptionInUserCollectionAsync(
            CancellationToken cancellationToken = default)
        {
            CollectionCalls++;
            return CollectionError is null
                ? Task.FromResult(InUserCollection)
                : Task.FromException<bool>(CollectionError);
        }

        public Task<SecretValue> GetCustomerPurchaseIdAsync(
            SecretValue serviceTicket,
            string publisherUserId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("service-ticket-value-with-enough-entropy", serviceTicket.DangerousGetString());
            Assert.Equal("10000000-0000-0000-0000-000000000001", publisherUserId);
            CollectionsKeyCalls++;
            events?.Add("purchase-key");
            return Task.FromResult(new SecretValue("store-purchase-key-with-enough-entropy".AsSpan()));
        }
    }

    private sealed class FakePrivilegeContext(bool elevated) : IPrivilegeContext
    {
        public bool IsElevated { get; } = elevated;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
