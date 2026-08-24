using System.Net;
using System.Text;
using System.Text.Json;
using LocaleSmith.Core.Models;
using LocaleSmith.Infrastructure.ModPlatform;
using LocaleSmith.Infrastructure.Security;

namespace LocaleSmith.Infrastructure.Tests;

public sealed class MicrosoftStoreBillingClientTests
{
    private const string Pat = "mctx_pat_billing-contract-secret";
    private const string Ticket = "service-ticket-value-with-enough-entropy";
    private const string StoreIdKey = "store-id-key-value-with-enough-entropy";
    private static readonly Guid UserId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task ServiceTicketUsesAuthenticatedSameOriginPostAndValidatesIdentifiers()
    {
        HttpRequestMessage? captured = null;
        using var handler = new StubHandler((request, _) =>
        {
            captured = request;
            return Task.FromResult(JsonResponse(
                $$"""
                {
                  "service_ticket": "{{Ticket}}",
                  "token_type": "Bearer",
                  "expires_at": "2026-08-24T12:05:00Z",
                  "publisher_user_id": "{{UserId}}",
                  "parent_store_id": "9NP8V6WQNGT0",
                  "subscription_store_id": "9N92NJ4D37P3"
                }
                """));
        });
        using var client = await CreateClientAsync(handler);

        using var result = await client.RequestMicrosoftStoreServiceTicketAsync(
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal(
            "https://mods.example/api/v1/me/billing/microsoft-store/service-ticket",
            captured.RequestUri?.AbsoluteUri);
        Assert.Equal(Pat, captured.Headers.Authorization?.Parameter);
        Assert.Equal(Ticket, result.Ticket.DangerousGetString());
        Assert.Equal(UserId, result.PublisherUserId);
        Assert.Equal(MicrosoftStoreBillingContract.ParentAppStoreId, result.ParentStoreId);
        Assert.Equal(MicrosoftStoreBillingContract.SubscriptionStoreId, result.SubscriptionStoreId);
    }

    [Fact]
    public async Task PaidCapabilitiesRequireExactPublicStoreCatalogMetadata()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            """
            {
              "service": "MCTX Mod Hub",
              "build_id": "test",
              "supported_api_majors": [1],
              "preferred_api_major": 1,
              "features": ["personal_access_token_v1", "forum_v1", "microsoft_store_billing_v1", "accelerated_downloads_v1"],
              "limits": {
                "max_mod_bytes": 2147483648,
                "upload_chunk_bytes": 8388608,
                "upload_concurrency": 3,
                "download_range_concurrency": 4
              },
              "turnstile": {"required": false, "site_key": null},
              "server_time": "2026-08-24T12:00:00Z",
              "microsoft_store": {
                "catalog_status": "draft",
                "parent_store_id": "9NP8V6WQNGT0",
                "subscription_store_id": "9N92NJ4D37P3",
                "internal_product_id": "localesmith_domestic_acceleration_monthly",
                "billing_period": "P1M",
                "trial_days": 7,
                "pricing": {
                  "base_currency": "USD",
                  "base_amount": "4.99",
                  "localized_by_store": true,
                  "china_currency": "CNY",
                  "china_amount": "30.00",
                  "introductory_price": null
                },
                "hidden_parent_app_only": true,
                "privacy_url": "https://dow.dzxh-tx.cn/privacy"
              }
            }
            """)));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var meta = await client.GetMetaAsync(TestContext.Current.CancellationToken);

        Assert.Contains(MicrosoftStoreBillingContract.AcceleratedDownloadsCapability, meta.Features);
        Assert.Equal("4.99", meta.MicrosoftStore?.Pricing.BaseAmount);
        Assert.Null(meta.MicrosoftStore?.Pricing.IntroductoryPrice);
    }

    [Fact]
    public async Task BillingFeatureWithoutCatalogMetadataFailsClosed()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            """
            {
              "service": "MCTX Mod Hub",
              "build_id": "test",
              "supported_api_majors": [1],
              "preferred_api_major": 1,
              "features": ["personal_access_token_v1", "forum_v1", "microsoft_store_billing_v1"],
              "limits": {
                "max_mod_bytes": 2147483648,
                "upload_chunk_bytes": 8388608,
                "upload_concurrency": 3,
                "download_range_concurrency": 4
              },
              "turnstile": {"required": false, "site_key": null},
              "server_time": "2026-08-24T12:00:00Z"
            }
            """)));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.GetMetaAsync(TestContext.Current.CancellationToken));

        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task ServiceTicketRejectsMismatchedStoreCatalog()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            $$"""
            {
              "service_ticket": "{{Ticket}}",
              "token_type": "Bearer",
              "expires_at": "2026-08-24T12:05:00Z",
              "publisher_user_id": "{{UserId}}",
              "parent_store_id": "wrong",
              "subscription_store_id": "9N92NJ4D37P3"
            }
            """)));
        using var client = await CreateClientAsync(handler);

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.RequestMicrosoftStoreServiceTicketAsync(TestContext.Current.CancellationToken));

        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task VerifySendsOnlyStoreIdKeyInBodyAndNeverInUri()
    {
        Uri? requestUri = null;
        string? authorization = null;
        string? body = null;
        using var handler = new StubHandler(async (request, cancellationToken) =>
        {
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization?.Parameter;
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("{}", HttpStatusCode.OK);
        });
        using var client = await CreateClientAsync(handler);
        using var storeIdKey = new SecretValue(StoreIdKey.AsSpan());

        await client.VerifyMicrosoftStorePurchaseAsync(
            storeIdKey,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "https://mods.example/api/v1/me/billing/microsoft-store/verify",
            requestUri?.AbsoluteUri);
        Assert.DoesNotContain(StoreIdKey, requestUri?.AbsoluteUri ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(Pat, authorization);
        using var document = JsonDocument.Parse(Assert.IsType<string>(body));
        var property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("store_id_key", property.Name);
        Assert.Equal(StoreIdKey, property.Value.GetString());
    }

    [Fact]
    public async Task EntitlementsUseServerTimeAndProviderNeutralDataEnvelope()
    {
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/me/entitlements", request.RequestUri?.AbsolutePath);
            return Task.FromResult(JsonResponse(
                $$"""
                {
                  "data": [{
                    "id": "20000000-0000-0000-0000-000000000001",
                    "entitlement_key": "domestic_download_acceleration",
                    "status": "active",
                    "valid_from": "2026-08-24T00:00:00Z",
                    "valid_until": "2026-09-24T00:00:00Z",
                    "last_verified_at": "2026-08-24T11:55:00Z",
                    "source_provider": "microsoft_store",
                    "source_product_id": "9N92NJ4D37P3",
                    "source_sku_id": "monthly-sku",
                    "source_status": "active"
                  }],
                  "server_time": "2026-08-24T12:00:00Z"
                }
                """));
        });
        using var client = await CreateClientAsync(handler);

        var result = await client.GetEntitlementsAsync(TestContext.Current.CancellationToken);

        var entitlement = Assert.Single(result.Data);
        Assert.True(entitlement.IsUsable(result.ServerTime));
    }

    [Fact]
    public async Task MissingPatPreventsAnyBillingNetworkRequest()
    {
        var calls = 0;
        using var handler = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse("{}"));
        });
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        using var client = new ModPlatformClient(
            httpClient,
            new Uri("https://mods.example/"),
            new SecretStoreModPlatformAccessTokenProvider(secrets));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetEntitlementsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DownloadSourcesExposeOnlyDefaultPathAndServerAvailabilityDecision()
    {
        var versionId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                $"/api/v1/files/{versionId:D}/download-sources",
                request.RequestUri?.AbsolutePath);
            Assert.Equal(Pat, request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse(
                $$"""
                {
                  "version_id": "{{versionId}}",
                  "filename": "demo.jar",
                  "size": 16,
                  "sha256": "{{new string('a', 64)}}",
                  "sources": [{
                    "id": "default",
                    "kind": "local_nginx",
                    "download_url": "/api/v1/files/{{versionId}}/download",
                    "supports_range": true
                  }],
                  "additional_source": {
                    "status": "available",
                    "reason_code": null,
                    "grant_url": "/api/v1/files/{{versionId}}/accelerated-download-grants",
                    "browser_parallel_range_enabled": true
                  }
                }
                """));
        });
        using var client = await CreateClientAsync(handler);

        var response = await client.GetDownloadSourcesAsync(
            versionId,
            TestContext.Current.CancellationToken);

        Assert.True(response.IsAccelerationAvailable);
        Assert.Equal("default", Assert.Single(response.Sources).Id);
        Assert.DoesNotContain("rains3", JsonSerializer.Serialize(response), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("entitlement_required")]
    [InlineData("entitlement_expired")]
    [InlineData("billing_verification_stale")]
    [InlineData("accelerated_source_unavailable")]
    public async Task DownloadSourcesAcceptOnlyStableUnavailableReasons(string reasonCode)
    {
        var versionId = Guid.NewGuid();
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            $$"""
            {
              "version_id": "{{versionId}}",
              "filename": "demo.jar",
              "size": 16,
              "sha256": "{{new string('a', 64)}}",
              "sources": [{
                "id": "default",
                "kind": "local_nginx",
                "download_url": "/api/v1/files/{{versionId}}/download",
                "supports_range": true
              }],
              "additional_source": {
                "status": "unavailable",
                "reason_code": "{{reasonCode}}",
                "grant_url": null
              }
            }
            """)));
        using var client = await CreateClientAsync(handler);

        var response = await client.GetDownloadSourcesAsync(
            versionId,
            TestContext.Current.CancellationToken);

        Assert.False(response.IsAccelerationAvailable);
        Assert.Equal(reasonCode, response.AdditionalSource.ReasonCode);
    }

    [Fact]
    public async Task GrantKeepsSeparateGetAndHeadSecretsInMemoryAndRedactsToString()
    {
        var versionId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        const string getUrl = "https://storage.example/private/demo.jar?method=get&signature=secret-get";
        const string headUrl = "https://storage.example/private/demo.jar?method=head&signature=secret-head";
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.DoesNotContain("signature", request.RequestUri?.AbsoluteUri ?? string.Empty, StringComparison.Ordinal);
            return Task.FromResult(JsonResponse(
                $$"""
                {
                  "grant_id": "{{grantId}}",
                  "version_id": "{{versionId}}",
                  "get_url": "{{getUrl}}",
                  "head_url": "{{headUrl}}",
                  "expires_at": "2026-08-24T12:10:00Z",
                  "fallback_url": "/api/v1/files/{{versionId}}/download",
                  "size": 16,
                  "sha256": "{{new string('a', 64)}}",
                  "supports_range": true,
                  "browser_parallel_range_enabled": true
                }
                """));
        });
        using var client = await CreateClientAsync(handler);
        var grant = await client.CreateAcceleratedDownloadGrantAsync(
            versionId,
            TestContext.Current.CancellationToken);

        Assert.Equal(getUrl, grant.DangerousGetUrl());
        Assert.Equal(headUrl, grant.DangerousGetHeadUrl());
        Assert.DoesNotContain("signature", grant.ToString(), StringComparison.Ordinal);

        grant.Dispose();
        Assert.Throws<ObjectDisposedException>(() => grant.DangerousGetUrl());
        Assert.Throws<ObjectDisposedException>(() => grant.DangerousGetHeadUrl());
    }

    [Fact]
    public async Task GrantRejectsDifferentGetAndHeadOrigins()
    {
        var versionId = Guid.NewGuid();
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            $$"""
            {
              "grant_id": "{{Guid.NewGuid()}}",
              "version_id": "{{versionId}}",
              "get_url": "https://storage.example/demo.jar?signature=get",
              "head_url": "https://attacker.example/demo.jar?signature=head",
              "expires_at": "2026-08-24T12:10:00Z",
              "fallback_url": "/api/v1/files/{{versionId}}/download",
              "size": 16,
              "sha256": "{{new string('a', 64)}}",
              "supports_range": true,
              "browser_parallel_range_enabled": true
            }
            """)));
        using var client = await CreateClientAsync(handler);

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.CreateAcceleratedDownloadGrantAsync(
                versionId,
                TestContext.Current.CancellationToken));

        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task VerifyFailureNeverEchoesStoreIdKeyFromServerMessage()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            JsonSerializer.Serialize(new { error = new { code = "bad", message = StoreIdKey } }),
            HttpStatusCode.BadRequest)));
        using var client = await CreateClientAsync(handler);
        using var key = new SecretValue(StoreIdKey.AsSpan());

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.VerifyMicrosoftStorePurchaseAsync(
                key,
                TestContext.Current.CancellationToken));

        Assert.Equal("store_credential_invalid", exception.Code);
        Assert.DoesNotContain(StoreIdKey, exception.ToString(), StringComparison.Ordinal);
    }

    private static async Task<ModPlatformClient> CreateClientAsync(HttpMessageHandler handler)
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            Pat.AsMemory(),
            TestContext.Current.CancellationToken);
        return new ModPlatformClient(
            new HttpClient(handler, disposeHandler: false),
            new Uri("https://mods.example/"),
            new SecretStoreModPlatformAccessTokenProvider(secrets));
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await callback(request, cancellationToken);
            if (request.RequestUri?.AbsolutePath.StartsWith("/api/v1/", StringComparison.Ordinal) == true)
            {
                response.Headers.TryAddWithoutValidation("X-API-Version", "1.0");
            }

            return response;
        }
    }
}
