using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using LocaleSmith.Infrastructure.ModPlatform;

namespace LocaleSmith.Infrastructure.Tests;

public sealed class ModPlatformArtifactDownloadCoordinatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-08-24T12:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("feature")]
    [InlineData("pat")]
    [InlineData("scope")]
    [InlineData("entitlement")]
    [InlineData("expired")]
    [InlineData("stale")]
    [InlineData("source")]
    public async Task RefusalMatrixNeverRequestsGrantOrPrivateStorage(string denial)
    {
        var fixture = CreateFixture();
        ConfigureDenial(fixture, denial);

        var availability = await fixture.Coordinator.GetAccelerationAvailabilityAsync(
            fixture.Artifact,
            TestContext.Current.CancellationToken);

        Assert.False(availability.IsAvailable);
        Assert.Equal(0, fixture.Client.GrantCalls);
        Assert.Equal(0, fixture.Accelerated.DownloadCalls);
    }

    [Fact]
    public async Task DeniedAccelerationFallsBackToExistingDefaultDownloader()
    {
        var fixture = CreateFixture();
        ConfigureDenial(fixture, "stale");

        var result = await fixture.Coordinator.DownloadAsync(
            fixture.Artifact,
            CreateDestinationPath(),
            ModPlatformDownloadRoute.DomesticAcceleration,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.FellBack);
        Assert.Equal(ModPlatformDownloadRoute.Default, result.ActualRoute);
        Assert.Equal(1, fixture.Default.DownloadCalls);
        Assert.Equal(0, fixture.Client.GrantCalls);
        Assert.Equal(0, fixture.Accelerated.DownloadCalls);
    }

    [Fact]
    public async Task AvailableAccelerationUsesServerGrantAndSkipsDefault()
    {
        var fixture = CreateFixture();

        var result = await fixture.Coordinator.DownloadAsync(
            fixture.Artifact,
            CreateDestinationPath(),
            ModPlatformDownloadRoute.DomesticAcceleration,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.FellBack);
        Assert.Equal(ModPlatformDownloadRoute.DomesticAcceleration, result.ActualRoute);
        Assert.Equal(1, fixture.Client.GrantCalls);
        Assert.Equal(1, fixture.Accelerated.DownloadCalls);
        Assert.Equal(0, fixture.Default.DownloadCalls);
    }

    [Fact]
    public async Task PrivateStorageFailureFallsBackWithoutLeakingSignedUrl()
    {
        var fixture = CreateFixture();
        fixture.Accelerated.Error = new AcceleratedDownloadException("accelerated_storage_unavailable");

        var result = await fixture.Coordinator.DownloadAsync(
            fixture.Artifact,
            CreateDestinationPath(),
            ModPlatformDownloadRoute.DomesticAcceleration,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.FellBack);
        Assert.Equal("accelerated_storage_unavailable", result.SafeReasonCode);
        Assert.DoesNotContain("signature", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Default.DownloadCalls);
    }

    [Fact]
    public async Task GrantRenewalRechecksFullServerGate()
    {
        var fixture = CreateFixture();
        fixture.Accelerated.RenewOnce = true;

        await fixture.Coordinator.DownloadAsync(
            fixture.Artifact,
            CreateDestinationPath(),
            ModPlatformDownloadRoute.DomesticAcceleration,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Client.MetaCalls);
        Assert.Equal(2, fixture.Client.SessionCalls);
        Assert.Equal(2, fixture.Client.EntitlementCalls);
        Assert.Equal(2, fixture.Client.SourceCalls);
        Assert.Equal(2, fixture.Client.GrantCalls);
    }

    [Fact]
    public async Task VersionMetadataMismatchPreventsGrantAndPrivateStorage()
    {
        var fixture = CreateFixture();
        fixture.Client.Sources = CreateSources(fixture.Artifact) with { Size = fixture.Artifact.Size + 1 };

        var availability = await fixture.Coordinator.GetAccelerationAvailabilityAsync(
            fixture.Artifact,
            TestContext.Current.CancellationToken);

        Assert.False(availability.IsAvailable);
        Assert.Equal(0, fixture.Client.GrantCalls);
        Assert.Equal(0, fixture.Accelerated.DownloadCalls);
    }

    private static Fixture CreateFixture()
    {
        var artifact = CreateArtifact();
        var client = new FakeBillingClient(artifact);
        var credentials = new FakeCredentialService { IsConfigured = true };
        var defaultDownloader = new FakeDefaultDownloader();
        var accelerated = new FakeAcceleratedDownloader();
        return new Fixture(
            artifact,
            client,
            credentials,
            defaultDownloader,
            accelerated,
            new ModPlatformArtifactDownloadCoordinator(
                client,
                credentials,
                defaultDownloader,
                accelerated));
    }

    private static void ConfigureDenial(Fixture fixture, string denial)
    {
        switch (denial)
        {
            case "feature":
                fixture.Client.Meta = CreateMeta(includeAcceleration: false);
                break;
            case "pat":
                fixture.Credentials.IsConfigured = false;
                break;
            case "scope":
                fixture.Client.Session = CreateSession(["profile:read"]);
                break;
            case "entitlement":
                fixture.Client.Entitlements = new MicrosoftStoreEntitlements([], Now);
                break;
            case "expired":
                fixture.Client.Sources = CreateSources(fixture.Artifact, "entitlement_expired");
                break;
            case "stale":
                fixture.Client.Sources = CreateSources(fixture.Artifact, "billing_verification_stale");
                break;
            case "source":
                fixture.Client.Sources = CreateSources(fixture.Artifact, "accelerated_source_unavailable");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(denial));
        }
    }

    private static ModPlatformMeta CreateMeta(bool includeAcceleration = true)
    {
        var features = new List<string>
        {
            "personal_access_token_v1",
            "forum_v1",
            MicrosoftStoreBillingContract.Capability
        };
        if (includeAcceleration)
        {
            features.Add(MicrosoftStoreBillingContract.AcceleratedDownloadsCapability);
        }

        return new ModPlatformMeta(
            "MCTX Mod Hub",
            "test",
            [1],
            1,
            features,
            new ModPlatformLimits(2_147_483_648, 8_388_608, 3, 4),
            new ModPlatformTurnstile(false, null),
            Now,
            MicrosoftStore: CreateStoreMeta());
    }

    private static MicrosoftStoreMeta CreateStoreMeta() => new(
        "draft",
        MicrosoftStoreBillingContract.ParentAppStoreId,
        MicrosoftStoreBillingContract.SubscriptionStoreId,
        MicrosoftStoreBillingContract.SubscriptionProductId,
        "P1M",
        7,
        new MicrosoftStorePricingMeta("USD", "4.99", true, "CNY", "30.00", null),
        true,
        MicrosoftStoreBillingContract.PrivacyPolicyUri.AbsoluteUri);

    private static ModPlatformAuthSession CreateSession(IReadOnlyList<string>? scopes = null) => new(
        new ModPlatformUser(Guid.NewGuid(), "alex", "user"),
        Now.AddDays(1),
        scopes ?? ["downloads:accelerated"]);

    private static MicrosoftStoreEntitlements CreateEntitlements() => new(
        [new MicrosoftStoreEntitlement(
            Guid.NewGuid(),
            MicrosoftStoreBillingContract.AccelerationEntitlementKey,
            "active",
            Now.AddDays(-1),
            Now.AddDays(1),
            Now.AddMinutes(-1),
            "microsoft_store",
            MicrosoftStoreBillingContract.SubscriptionStoreId,
            "monthly",
            "active")],
        Now);

    private static ModPlatformDownloadSources CreateSources(
        ModPlatformVersion artifact,
        string? unavailableReason = null) => new(
            artifact.Id,
            artifact.Filename,
            artifact.Size,
            artifact.Sha256,
            [new ModPlatformDownloadSource("default", "local_nginx", artifact.DownloadUrl, true)],
            unavailableReason is null
                ? new ModPlatformAdditionalDownloadSource(
                    "available",
                    null,
                    $"/api/v1/files/{artifact.Id:D}/accelerated-download-grants",
                    true)
                : new ModPlatformAdditionalDownloadSource(
                    "unavailable",
                    unavailableReason,
                    null));

    private static ModPlatformVersion CreateArtifact() => new(
        Guid.Parse("30000000-0000-0000-0000-000000000001"),
        "1.0.0",
        ["1.21.1"],
        ["fabric"],
        string.Empty,
        "demo.jar",
        16,
        new string('a', 64),
        0,
        Now,
        "/api/v1/files/30000000-0000-0000-0000-000000000001/download");

    private static string CreateDestinationPath() => Path.Combine(
        Path.GetTempPath(),
        $"localesmith-coordinator-{Guid.NewGuid():N}.jar");

    private sealed class FakeBillingClient(ModPlatformVersion artifact) : IModPlatformBillingClient
    {
        public ModPlatformMeta Meta { get; set; } = CreateMeta();

        public ModPlatformAuthSession Session { get; set; } = CreateSession();

        public MicrosoftStoreEntitlements Entitlements { get; set; } = CreateEntitlements();

        public ModPlatformDownloadSources Sources { get; set; } = CreateSources(artifact);

        public int MetaCalls { get; private set; }

        public int SessionCalls { get; private set; }

        public int EntitlementCalls { get; private set; }

        public int SourceCalls { get; private set; }

        public int GrantCalls { get; private set; }

        public Task<ModPlatformMeta> GetMetaAsync(CancellationToken cancellationToken = default)
        {
            MetaCalls++;
            return Task.FromResult(Meta);
        }

        public Task<ModPlatformAuthSession> GetAuthenticatedSessionAsync(
            CancellationToken cancellationToken = default)
        {
            SessionCalls++;
            return Task.FromResult(Session);
        }

        public Task<MicrosoftStoreEntitlements> GetEntitlementsAsync(
            CancellationToken cancellationToken = default)
        {
            EntitlementCalls++;
            return Task.FromResult(Entitlements);
        }

        public Task<ModPlatformDownloadSources> GetDownloadSourcesAsync(
            Guid versionId,
            CancellationToken cancellationToken = default)
        {
            SourceCalls++;
            return Task.FromResult(Sources);
        }

        public Task<ModPlatformAcceleratedDownloadGrant> CreateAcceleratedDownloadGrantAsync(
            Guid versionId,
            CancellationToken cancellationToken = default)
        {
            GrantCalls++;
            return Task.FromResult(new ModPlatformAcceleratedDownloadGrant(
                Guid.NewGuid(),
                versionId,
                "https://storage.example/demo.jar?method=get&signature=secret".AsSpan(),
                "https://storage.example/demo.jar?method=head&signature=secret".AsSpan(),
                Now.AddMinutes(10),
                artifact.DownloadUrl,
                artifact.Size,
                artifact.Sha256,
                true,
                true));
        }

        public Task<MicrosoftStoreServiceTicket> RequestMicrosoftStoreServiceTicketAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task VerifyMicrosoftStorePurchaseAsync(
            SecretValue storeIdKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeCredentialService : IModPlatformCredentialService
    {
        public bool IsConfigured { get; set; }

        public ValueTask<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(IsConfigured);

        public ValueTask SaveAsync(
            ReadOnlyMemory<char> token,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<bool> DeleteAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class FakeDefaultDownloader : IModPlatformArtifactDownloader
    {
        public int DownloadCalls { get; private set; }

        public Task DownloadAsync(
            ModPlatformVersion artifact,
            string destinationPath,
            IProgress<ModPlatformDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAcceleratedDownloader : IModPlatformAcceleratedArtifactDownloader
    {
        public int DownloadCalls { get; private set; }

        public bool RenewOnce { get; set; }

        public Exception? Error { get; set; }

        public async Task DownloadAsync(
            ModPlatformVersion artifact,
            ModPlatformAcceleratedDownloadGrant initialGrant,
            Func<CancellationToken, Task<ModPlatformAcceleratedDownloadGrant>> renewGrantAsync,
            string destinationPath,
            IProgress<ModPlatformDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            if (RenewOnce)
            {
                using var renewed = await renewGrantAsync(cancellationToken);
            }

            if (Error is not null)
            {
                throw Error;
            }
        }
    }

    private sealed record Fixture(
        ModPlatformVersion Artifact,
        FakeBillingClient Client,
        FakeCredentialService Credentials,
        FakeDefaultDownloader Default,
        FakeAcceleratedDownloader Accelerated,
        ModPlatformArtifactDownloadCoordinator Coordinator);
}
