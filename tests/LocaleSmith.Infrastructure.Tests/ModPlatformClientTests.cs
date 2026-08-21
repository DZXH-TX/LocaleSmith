using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocaleSmith.Core.Models;
using LocaleSmith.Infrastructure.ModPlatform;
using LocaleSmith.Infrastructure.Security;

namespace LocaleSmith.Infrastructure.Tests;

public sealed class ModPlatformClientTests
{
    [Fact]
    public async Task ApplicationLoginSendsUsernamePasswordAndBearerApplicationToken()
    {
        const string username = "Steve_01";
        const string password = "correct horse battery staple 7";
        const string applicationToken = "mctx_pat_application-secret";
        var userId = Guid.NewGuid();
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://mods.example/api/v1/auth/application-login",
                request.RequestUri?.AbsoluteUri);
            Assert.DoesNotContain(password, request.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
            Assert.Equal($"Bearer {applicationToken}", request.Headers.Authorization?.ToString());
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            Assert.Equal("utf-8", request.Content?.Headers.ContentType?.CharSet);
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(
                $$"""
                {
                  "user": {
                    "id": "{{userId}}",
                    "username": "{{username}}",
                    "role": "user"
                  },
                  "csrf_token": null,
                  "expires_at": "2026-08-16T00:00:00Z",
                  "scopes": ["mods:read", "forum:write"]
                }
                """);
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var session = await client.VerifyApplicationLoginAsync(
            username,
            password.AsMemory(),
            applicationToken.AsMemory(),
            TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(Assert.IsType<string>(requestJson));
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        Assert.Equal(username, body.RootElement.GetProperty("username").GetString());
        Assert.Equal(password, body.RootElement.GetProperty("password").GetString());
        Assert.Equal(userId, session.User.Id);
        Assert.Equal(username, session.User.Username);
        Assert.Equal("user", session.User.Role);
        Assert.Equal(
            DateTimeOffset.Parse(
                "2026-08-16T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            session.ExpiresAt);
        Assert.Equal(["mods:read", "forum:write"], session.Scopes);
    }

    [Fact]
    public async Task ApplicationLoginMapsExplicitEmptyScopesWithoutGrantingCapabilities()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(
            $$"""
            {
              "user": {
                "id": "{{Guid.NewGuid()}}",
                "username": "ProfileOnly",
                "role": "user"
              },
              "csrf_token": null,
              "expires_at": null,
              "scopes": []
            }
            """)));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var session = await client.VerifyApplicationLoginAsync(
            "ProfileOnly",
            "correct-password".AsMemory(),
            "mctx_pat_application-secret".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.Empty(session.Scopes);
    }

    [Fact]
    public async Task ApplicationLoginDoesNotExposePasswordThroughUriOrServerControlledError()
    {
        const string password = "NeverLeakThisPassword7";
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(
                "https://mods.example/api/v1/auth/application-login",
                request.RequestUri?.AbsoluteUri);
            Assert.DoesNotContain(password, request.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "error": {
                        "code": "{{password}}",
                        "message": "{{password}}"
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.VerifyApplicationLoginAsync(
                "Steve_01",
                password.AsMemory(),
                "mctx_pat_application-secret".AsMemory(),
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("unauthorized", exception.Code);
        Assert.Equal(
            "The Mod platform rejected the application token.",
            exception.Message);
        Assert.DoesNotContain(password, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "invalid_credentials", "invalid_credentials")]
    [InlineData(HttpStatusCode.Unauthorized, "forbidden", "unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden", "forbidden")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited", "rate_limited")]
    public async Task ApplicationLoginAcceptsOnlyStatusBoundStableErrorCodes(
        HttpStatusCode statusCode,
        string serverCode,
        string expectedCode)
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        error = new { code = serverCode, message = "untrusted detail" }
                    }),
                    Encoding.UTF8,
                    "application/json")
            }));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.VerifyApplicationLoginAsync(
                "Steve_01",
                "correct-password".AsMemory(),
                "mctx_pat_application-secret".AsMemory(),
                TestContext.Current.CancellationToken));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain("untrusted detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticatedSessionRestoresUserWithStoredApplicationToken()
    {
        var userId = Guid.NewGuid();
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "https://mods.example/api/v1/auth/session",
                request.RequestUri?.AbsoluteUri);
            Assert.Equal(
                "Bearer mctx_pat_stored-application",
                request.Headers.Authorization?.ToString());
            Assert.Null(request.Content);
            return Task.FromResult(JsonResponse(
                $$"""
                {
                  "user": {
                    "id": "{{userId}}",
                    "username": "Alex-01",
                    "role": "admin"
                  },
                  "csrf_token": null,
                  "expires_at": null
                }
                """));
        });
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_stored-application".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);

        var session = await client.GetAuthenticatedSessionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(userId, session.User.Id);
        Assert.Equal("Alex-01", session.User.Username);
        Assert.Equal("admin", session.User.Role);
        Assert.Null(session.ExpiresAt);
        Assert.Empty(session.Scopes);
    }

    [Fact]
    public async Task LegacyApplicationTokenVerificationUsesSuppliedPatWithoutSendingPassword()
    {
        var userId = Guid.NewGuid();
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "https://mods.example/api/v1/auth/session",
                request.RequestUri?.AbsoluteUri);
            Assert.Equal(
                "Bearer mctx_pat_legacy-application",
                request.Headers.Authorization?.ToString());
            Assert.Null(request.Content);
            return Task.FromResult(JsonResponse(
                $$"""
                {
                  "user": {
                    "id": "{{userId}}",
                    "username": "Canonical_User",
                    "role": "user"
                  },
                  "csrf_token": null,
                  "expires_at": null,
                  "scopes": ["mods:read", "forum:read"]
                }
                """));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var session = await client.VerifyApplicationTokenAsync(
            "canonical_user",
            "mctx_pat_legacy-application".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.Equal(userId, session.User.Id);
        Assert.Equal("Canonical_User", session.User.Username);
        Assert.Equal(["mods:read", "forum:read"], session.Scopes);
    }

    [Fact]
    public async Task LegacyApplicationTokenVerificationRejectsUsernameMismatch()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(
            $$"""
            {
              "user": {
                "id": "{{Guid.NewGuid()}}",
                "username": "TokenOwner",
                "role": "user"
              },
              "csrf_token": null,
              "expires_at": null
            }
            """)));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.VerifyApplicationTokenAsync(
                "DifferentUser",
                "mctx_pat_legacy-application".AsMemory(),
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("invalid_credentials", exception.Code);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("*")]
    public async Task ApplicationLoginRejectsControlledHighPrivilegeScopes(string scope)
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(
            $$"""
            {
              "user": {
                "id": "{{Guid.NewGuid()}}",
                "username": "TokenOwner",
                "role": "admin"
              },
              "csrf_token": null,
              "expires_at": null,
              "scopes": ["{{scope}}"]
            }
            """)));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.VerifyApplicationLoginAsync(
                "TokenOwner",
                "correct-password".AsMemory(),
                "mctx_pat_application-secret".AsMemory(),
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal("forbidden", exception.Code);
    }

    [Theory]
    [InlineData("missing_user")]
    [InlineData("csrf_for_pat")]
    [InlineData("empty_user_id")]
    [InlineData("unknown_role")]
    [InlineData("default_expiry")]
    [InlineData("duplicate_scopes")]
    [InlineData("blank_scope")]
    public async Task RejectsInvalidAuthenticatedSessionContract(string variant)
    {
        var userId = variant == "empty_user_id" ? Guid.Empty : Guid.NewGuid();
        var role = variant == "unknown_role" ? "moderator" : "user";
        var csrfToken = variant == "csrf_for_pat" ? "\"browser-csrf\"" : "null";
        var expiresAt = variant == "default_expiry"
            ? "\"0001-01-01T00:00:00+00:00\""
            : "null";
        var scopes = variant switch
        {
            "duplicate_scopes" => "[\"mods:read\",\"mods:read\"]",
            "blank_scope" => "[\"\"]",
            _ => "[]"
        };
        var json = variant == "missing_user"
            ? """{"csrf_token":null,"expires_at":null}"""
            : $$"""
              {
                "user": {
                  "id": "{{userId}}",
                  "username": "Steve_01",
                  "role": "{{role}}"
                },
                "csrf_token": {{csrfToken}},
                "expires_at": {{expiresAt}},
                "scopes": {{scopes}}
              }
              """;
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(json)));
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_contract-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.GetAuthenticatedSessionAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task GetsMetaAndMapsArtifactAndForwardCompatibleReportingCapabilities()
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://mods.example/api/meta", request.RequestUri?.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(JsonResponse(
                """
                {
                  "service": "MCTX Mod Hub",
                  "build_id": "test",
                  "supported_api_majors": [1],
                  "preferred_api_major": 1,
                  "features": [
                    "opaque_session_v1",
                    "personal_access_token_v1",
                    "resumable_upload_v1",
                    "range_download_v1",
                    "forum_v1",
                    "artifact_types_v1",
                    "action_nonce_v1",
                    "content_reports_v1"
                  ],
                  "limits": {
                    "max_mod_bytes": 2147483648,
                    "upload_chunk_bytes": 8388608,
                    "upload_concurrency": 3,
                    "download_range_concurrency": 4
                  },
                  "artifacts": {
                    "allowed_extensions": [".jar", ".zip"],
                    "allowed_mime_types": ["application/java-archive", "application/zip"],
                    "validation": ["sha256", "zip_magic", "zip_central_directory"]
                  },
                  "reporting": {
                    "terms_url": "https://dow.dzxh-tx.cn/terms",
                    "community_guidelines_url": "https://dow.dzxh-tx.cn/community-guidelines",
                    "target_types": ["mod", "mod_version", "forum_thread", "forum_post", "user", "collection"],
                    "categories": ["spam", "harassment", "hate_speech", "sexual_content", "violence", "illegal_content", "malware", "copyright", "privacy", "impersonation", "child_safety", "other", "misinformation"]
                  },
                  "turnstile": {"required": false, "site_key": null},
                  "server_time": "2026-08-15T00:00:00Z"
                }
                """));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var meta = await client.GetMetaAsync(TestContext.Current.CancellationToken);

        Assert.Contains("artifact_types_v1", meta.Features);
        Assert.NotNull(meta.Artifacts);
        Assert.Equal([".jar", ".zip"], meta.Artifacts.AllowedExtensions);
        Assert.Equal(["application/java-archive", "application/zip"], meta.Artifacts.AllowedMimeTypes);
        Assert.Equal(["sha256", "zip_magic", "zip_central_directory"], meta.Artifacts.Validation);
        Assert.Contains("content_reports_v1", meta.Features);
        Assert.NotNull(meta.Reporting);
        Assert.Equal("https://dow.dzxh-tx.cn/terms", meta.Reporting.TermsUrl);
        Assert.Equal(
            ["mod", "mod_version", "forum_thread", "forum_post", "user", "collection"],
            meta.Reporting.TargetTypes);
        Assert.Contains("child_safety", meta.Reporting.Categories);
        Assert.Contains("misinformation", meta.Reporting.Categories);
    }

    [Theory]
    [InlineData("wrong_service")]
    [InlineData("missing_v1")]
    [InlineData("missing_forum_feature")]
    [InlineData("missing_pat_feature")]
    [InlineData("duplicate_feature")]
    public async Task RejectsMetaThatDoesNotIdentifyTheWebsiteV1CommunityContract(string variant)
    {
        var json = ValidMinimalMetaJson();
        json = variant switch
        {
            "wrong_service" => json.Replace("MCTX Mod Hub", "Another Service", StringComparison.Ordinal),
            "missing_v1" => json
                .Replace("\"supported_api_majors\": [1]", "\"supported_api_majors\": [2]", StringComparison.Ordinal)
                .Replace("\"preferred_api_major\": 1", "\"preferred_api_major\": 2", StringComparison.Ordinal),
            "missing_forum_feature" => json.Replace("\"forum_v1\"", "\"unknown_forum\"", StringComparison.Ordinal),
            "missing_pat_feature" => json.Replace(
                "\"personal_access_token_v1\"",
                "\"unknown_auth\"",
                StringComparison.Ordinal),
            "duplicate_feature" => json.Replace(
                "\"forum_v1\"",
                "\"forum_v1\", \"forum_v1\"",
                StringComparison.Ordinal),
            _ => throw new InvalidOperationException("Unknown meta contract variant.")
        };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(json)));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.GetMetaAsync(TestContext.Current.CancellationToken));

        Assert.Equal("invalid_response", exception.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2.0")]
    [InlineData("1.0, 2.0")]
    public async Task RejectsMissingOrWrongApiVersionHeaderOnVersionedJsonSuccess(string? declaredVersion)
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = JsonResponse("""{"data":[],"page":1,"page_size":20,"total":0}""");
            if (declaredVersion is not null)
            {
                response.Headers.TryAddWithoutValidation(
                    ModPlatformApiContract.ApiVersionHeaderName,
                    declaredVersion);
            }

            return Task.FromResult(response);
        }, addApiVersionHeader: false);
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.GetModsAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task MissingApiVersionHeaderTakesPrecedenceOverErrorEnvelope()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"error":{"code":"not_found","message":"不存在"}}""",
                Encoding.UTF8,
                "application/json")
        }), addApiVersionHeader: false);
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.GetModAsync("missing", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task VersionedPublicJsonReadRequiresOkStatus()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(
            """{"data":[],"page":1,"page_size":20,"total":0}""",
            HttpStatusCode.PartialContent)));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.GetModsAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.PartialContent, exception.StatusCode);
        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task ListsModsThroughVersionedEndpointAndMapsSnakeCaseContract()
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "https://mods.example/api/v1/mods?page=2&page_size=10&sort=downloads&q=sodium%20plus&tag=performance",
                request.RequestUri?.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(JsonResponse(
                """
                {
                  "data": [],
                  "page": 2,
                  "page_size": 10,
                  "total": 0
                }
                """));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var result = await client.GetModsAsync(
            new ModPlatformSearchOptions(
                Page: 2,
                PageSize: 10,
                Query: "sodium plus",
                Tag: "performance",
                Sort: "downloads"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Page);
        Assert.Equal(10, result.PageSize);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task AllowsTrimmedFilterValuesAtWebsiteUnicodeRuneLimits()
    {
        var query = string.Concat(Enumerable.Repeat("😀", 100));
        var tag = new string('标', 64);
        var loader = new string('载', 32);
        var gameVersion = new string('版', 32);
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(
                $"https://mods.example/api/v1/mods?page=1&page_size=20&sort=recent&q={Uri.EscapeDataString(query)}&tag={Uri.EscapeDataString(tag)}&loader={Uri.EscapeDataString(loader)}&game_version={Uri.EscapeDataString(gameVersion)}",
                request.RequestUri?.AbsoluteUri);
            return Task.FromResult(JsonResponse(
                """{"data":[],"page":1,"page_size":20,"total":0}"""));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        await client.GetModsAsync(
            new ModPlatformSearchOptions(
                Query: $"  {query}  ",
                Tag: $" {tag} ",
                Loader: $"\t{loader}\t",
                GameVersion: $"\r\n{gameVersion}\r\n"),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task IgnoresWhitespaceOnlyFilterValues()
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(
                "https://mods.example/api/v1/mods?page=1&page_size=20&sort=recent",
                request.RequestUri?.AbsoluteUri);
            return Task.FromResult(JsonResponse(
                """{"data":[],"page":1,"page_size":20,"total":0}"""));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        await client.GetModsAsync(
            new ModPlatformSearchOptions(
                Query: " \t\r\n ",
                Tag: "  ",
                Loader: "\t",
                GameVersion: "\r\n"),
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("Query", 100)]
    [InlineData("Tag", 64)]
    [InlineData("Loader", 32)]
    [InlineData("GameVersion", 32)]
    public async Task RejectsFilterValuesBeyondWebsiteRuneLimits(string filter, int maximumLength)
    {
        var invoked = false;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            invoked = true;
            return Task.FromResult(JsonResponse(
                """{"data":[],"page":1,"page_size":20,"total":0}"""));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));
        var value = string.Concat(Enumerable.Repeat("😀", maximumLength + 1));
        var options = filter switch
        {
            "Query" => new ModPlatformSearchOptions(Query: value),
            "Tag" => new ModPlatformSearchOptions(Tag: value),
            "Loader" => new ModPlatformSearchOptions(Loader: value),
            "GameVersion" => new ModPlatformSearchOptions(GameVersion: value),
            _ => throw new InvalidOperationException("Unknown filter test case.")
        };

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.GetModsAsync(options, TestContext.Current.CancellationToken));

        Assert.Equal(filter, exception.ParamName);
        Assert.False(invoked);
    }

    [Theory]
    [InlineData("Query")]
    [InlineData("Tag")]
    [InlineData("Loader")]
    [InlineData("GameVersion")]
    public async Task RejectsEmbeddedControlCharactersInFilters(string filter)
    {
        var invoked = false;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            invoked = true;
            return Task.FromResult(JsonResponse(
                """{"data":[],"page":1,"page_size":20,"total":0}"""));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));
        const string value = "safe\0unsafe";
        var options = filter switch
        {
            "Query" => new ModPlatformSearchOptions(Query: value),
            "Tag" => new ModPlatformSearchOptions(Tag: value),
            "Loader" => new ModPlatformSearchOptions(Loader: value),
            "GameVersion" => new ModPlatformSearchOptions(GameVersion: value),
            _ => throw new InvalidOperationException("Unknown filter test case.")
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetModsAsync(options, TestContext.Current.CancellationToken));

        Assert.Equal(filter, exception.ParamName);
        Assert.False(invoked);
    }

    [Fact]
    public async Task AcceptsWebsiteCompatibleModAndForumResponseContracts()
    {
        var modId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var postId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var tagJson = TagJson(tagId);
        var versionJson = VersionJson(versionId);
        var summaryJson = ModSummaryJson(modId, tagJson, versionJson);
        var threadJson = ThreadJson(threadId, modId, authorId);
        var postJson = PostJson(postId, threadId, authorId);
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            var path = request.RequestUri?.AbsolutePath;
            var json = path switch
            {
                "/api/v1/tags" => $"[{tagJson}]",
                "/api/v1/mods" => $$"""{"data":[{{summaryJson}}],"page":1,"page_size":20,"total":1}""",
                var value when value == $"/api/v1/mods/{modId:D}" =>
                    ModDetailJson(modId, tagJson, versionJson),
                var value when value == $"/api/v1/mods/{modId:D}/threads" =>
                    $$"""{"data":[{{threadJson}}],"page":1,"page_size":30,"total":1}""",
                var value when value == $"/api/v1/threads/{threadId:D}" => threadJson,
                var value when value == $"/api/v1/threads/{threadId:D}/posts" =>
                    $$"""{"data":[{{postJson}}],"page":1,"page_size":30,"total":1}""",
                _ => throw new InvalidOperationException($"Unexpected response-contract path: {path}")
            };
            return Task.FromResult(JsonResponse(json));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var tags = await client.GetTagsAsync(TestContext.Current.CancellationToken);
        var mods = await client.GetModsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var detail = await client.GetModAsync(modId.ToString("D"), TestContext.Current.CancellationToken);
        var threads = await client.GetThreadsAsync(modId, cancellationToken: TestContext.Current.CancellationToken);
        var thread = await client.GetThreadAsync(threadId, TestContext.Current.CancellationToken);
        var posts = await client.GetPostsAsync(threadId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(tagId, Assert.Single(tags).Id);
        Assert.Equal(modId, Assert.Single(mods.Data).Id);
        Assert.Equal(Guid.Parse("50000000-0000-0000-0000-000000000001"), Assert.Single(mods.Data).OwnerId);
        Assert.Equal(versionId, Assert.Single(detail.Versions).Id);
        Assert.Equal(Guid.Parse("50000000-0000-0000-0000-000000000001"), detail.OwnerId);
        Assert.Equal(threadId, Assert.Single(threads.Data).Id);
        Assert.Equal(modId, thread.ModId);
        Assert.Equal(postId, Assert.Single(posts.Data).Id);
    }

    [Theory]
    [InlineData("missing_owner")]
    [InlineData("non_public_status")]
    public async Task RejectsPublicModResponseOutsideWebsiteContract(string variant)
    {
        var modId = Guid.NewGuid();
        var summary = System.Text.Json.Nodes.JsonNode.Parse(
            ModSummaryJson(modId, TagJson(Guid.NewGuid()), VersionJson(Guid.NewGuid())))!.AsObject();
        if (variant == "missing_owner")
        {
            summary.Remove("owner_id");
        }
        else
        {
            summary["status"] = "pending_review";
        }

        var page = new System.Text.Json.Nodes.JsonObject
        {
            ["data"] = new System.Text.Json.Nodes.JsonArray(summary),
            ["page"] = 1,
            ["page_size"] = 20,
            ["total"] = 1
        };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(page.ToJsonString())));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.GetModsAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task RejectsForumThreadStatusOutsideOpenOrClosed()
    {
        var threadId = Guid.NewGuid();
        var json = ThreadJson(threadId, Guid.NewGuid(), Guid.NewGuid())
            .Replace("\"status\": \"open\"", "\"status\": \"hidden\"", StringComparison.Ordinal);
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(json)));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.GetThreadAsync(threadId, TestContext.Current.CancellationToken));

        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task CreatesForumPostWithPatResolvedFromSecretStore()
    {
        var threadId = Guid.NewGuid();
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal($"https://mods.example/api/v1/threads/{threadId:D}/posts", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer mctx_pat_test-secret", request.Headers.Authorization?.ToString());
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(
                $$"""
                {
                  "id": "{{Guid.NewGuid()}}",
                  "thread_id": "{{threadId}}",
                  "author_id": "{{Guid.NewGuid()}}",
                  "author_name": "alex",
                  "content_markdown": "Useful reply",
                  "created_at": "2026-08-15T00:00:00Z",
                  "updated_at": "2026-08-15T00:00:00Z"
                }
                """,
                HttpStatusCode.Created);
        });
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_test-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);

        var post = await client.CreatePostAsync(
            threadId,
            " Useful reply ",
            TestContext.Current.CancellationToken);

        Assert.Equal(threadId, post.ThreadId);
        Assert.Equal("Useful reply", post.ContentMarkdown);
        Assert.Contains("\"content\":\"Useful reply\"", requestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatesForumThreadWithPatAndCreatedResponse()
    {
        var modId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"https://mods.example/api/v1/mods/{modId:D}/threads", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer mctx_pat_forum-secret", request.Headers.Authorization?.ToString());
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("X-CSRF-Token"));
            Assert.False(request.Headers.Contains("X-Request-Nonce"));
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(
                ThreadJson(threadId, modId, authorId),
                HttpStatusCode.Created);
        });
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_forum-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);

        var thread = await client.CreateThreadAsync(
            modId,
            " Translation feedback ",
            " Please review the terminology. ",
            TestContext.Current.CancellationToken);

        Assert.Equal(threadId, thread.Id);
        Assert.Contains("\"title\":\"Translation feedback\"", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"Please review the terminology.\"", requestJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("thread")]
    [InlineData("post")]
    public async Task ForumWritesRequireCreatedStatus(string operation)
    {
        var modId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(
            operation == "thread"
                ? ThreadJson(threadId, modId, authorId)
                : PostJson(Guid.NewGuid(), threadId, authorId))));
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_forum-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);

        Func<Task> action = operation == "thread"
            ? () => client.CreateThreadAsync(
                modId,
                "Translation feedback",
                "Please review the terminology.",
                TestContext.Current.CancellationToken)
            : () => client.CreatePostAsync(
                threadId,
                "Please review the terminology.",
                TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<ModPlatformException>(action);

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task CreatesCanonicalContentReportWithPatAndExactContract()
    {
        var reportId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://mods.example/api/v1/reports", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer mctx_pat_report-secret", request.Headers.Authorization?.ToString());
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(
                $$"""
                {
                  "id": "{{reportId}}",
                  "target_type": "mod_version",
                  "target_id": "{{targetId}}",
                  "category": "malware",
                  "status": "open",
                  "created_at": "2026-08-15T00:00:00Z"
                }
                """,
                HttpStatusCode.Created);
        });
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_report-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);

        var report = await client.CreateReportAsync(
            new ModPlatformReportRequest(
                ModPlatformReportTargetTypes.ModVersion,
                targetId,
                ModPlatformReportCategories.Malware,
                "  Archive contains an executable payload.  "),
            TestContext.Current.CancellationToken);

        Assert.Equal(reportId, report.Id);
        Assert.Equal(targetId, report.TargetId);
        Assert.Equal("open", report.Status);
        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestJson));
        var root = payload.RootElement;
        Assert.Equal("mod_version", root.GetProperty("target_type").GetString());
        Assert.Equal(targetId, root.GetProperty("target_id").GetGuid());
        Assert.Equal("malware", root.GetProperty("category").GetString());
        Assert.Equal(
            "Archive contains an executable payload.",
            root.GetProperty("details").GetString());
    }

    [Fact]
    public async Task CanonicalReportRequiresCreatedResponse()
    {
        var reportId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(
            $$"""
            {
              "id": "{{reportId}}",
              "target_type": "forum_post",
              "target_id": "{{targetId}}",
              "category": "spam",
              "status": "open",
              "created_at": "2026-08-15T00:00:00Z"
            }
            """)));
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_report-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);

        var exception = await Assert.ThrowsAsync<ModPlatformException>(() => client.CreateReportAsync(
            new ModPlatformReportRequest("forum_post", targetId, "spam", "Repeated advertisement"),
            TestContext.Current.CancellationToken));

        Assert.Equal("invalid_response", exception.Code);
    }

    [Theory]
    [InlineData("mod", false, "spam", "open")]
    [InlineData("forum_post", true, "spam", "open")]
    [InlineData("forum_post", false, "other", "open")]
    [InlineData("forum_post", false, "spam", "pending")]
    public async Task CanonicalReportRejectsMismatchedOrNonOpenResponse(
        string responseTargetType,
        bool useDifferentTargetId,
        string responseCategory,
        string responseStatus)
    {
        var reportId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var responseTargetId = useDifferentTargetId ? Guid.NewGuid() : targetId;
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(
            $$"""
            {
              "id": "{{reportId}}",
              "target_type": "{{responseTargetType}}",
              "target_id": "{{responseTargetId}}",
              "category": "{{responseCategory}}",
              "status": "{{responseStatus}}",
              "created_at": "2026-08-15T00:00:00Z"
            }
            """,
            HttpStatusCode.Created)));
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_report-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);

        var exception = await Assert.ThrowsAsync<ModPlatformException>(() => client.CreateReportAsync(
            new ModPlatformReportRequest("forum_post", targetId, "spam", "Repeated advertisement"),
            TestContext.Current.CancellationToken));

        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task CanonicalReportRejectsInvalidTargetCategoryAndDetailsBeforeNetwork()
    {
        var invoked = false;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            invoked = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateReportAsync(
            new ModPlatformReportRequest("post", Guid.NewGuid(), "spam", "valid details"),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateReportAsync(
            new ModPlatformReportRequest("forum_post", Guid.NewGuid(), "unknown", "valid details"),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CreateReportAsync(
            new ModPlatformReportRequest("forum_post", Guid.NewGuid(), "spam", "bad"),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CreateReportAsync(
            new ModPlatformReportRequest("forum_post", Guid.NewGuid(), "spam", new string('x', 1_901)),
            TestContext.Current.CancellationToken));

        Assert.False(invoked);
    }

    [Fact]
    public async Task ReportsForumPostWithForumWritePatAndNoContentResponse()
    {
        var postId = Guid.NewGuid();
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"https://mods.example/api/v1/posts/{postId:D}/reports", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer mctx_pat_report-secret", request.Headers.Authorization?.ToString());
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_report-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);

        await client.ReportPostAsync(
            postId,
            "  Contains prohibited executable links.  ",
            TestContext.Current.CancellationToken);

        Assert.Contains(
            "\"reason\":\"Contains prohibited executable links.\"",
            requestJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsUnexpectedBodyResponseForForumReport()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("{}")));
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_report-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.ReportPostAsync(
                Guid.NewGuid(),
                "Contains prohibited executable links.",
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task RejectsForumReportReasonOutsideWebsiteLimits()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.ReportPostAsync(
                Guid.NewGuid(),
                "bad",
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.ReportPostAsync(
                Guid.NewGuid(),
                new string('x', 2_001),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VersionedNoContentResponseRequiresApiVersionHeader()
    {
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)),
            addApiVersionHeader: false);
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_report-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.ReportPostAsync(
                Guid.NewGuid(),
                "Contains prohibited executable links.",
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NoContent, exception.StatusCode);
        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task RawUploadChunkResponseRequiresApiVersionHeader()
    {
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)),
            addApiVersionHeader: false);
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_upload-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);
        var upload = new ModPlatformUploadSession(
            Guid.NewGuid(),
            "translations.zip",
            4,
            4,
            1,
            new string('a', 64),
            "uploading",
            [],
            DateTimeOffset.Parse("2026-08-16T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await using var content = new MemoryStream([1, 2, 3, 4]);

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.UploadChunkAsync(
                upload,
                0,
                content,
                new string('b', 64),
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NoContent, exception.StatusCode);
        Assert.Equal("invalid_response", exception.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task RejectsEmptyMalformedNullAndIncompleteSuccessJson(string json)
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(json)));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.GetMetaAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task CallsCompleteZipUploadWorkflowWithPatAndFixedChunkHeaders()
    {
        var uploadId = Guid.NewGuid();
        var modId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var requestIndex = 0;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestIndex++;
            Assert.Equal("Bearer mctx_pat_upload-secret", request.Headers.Authorization?.ToString());
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("X-CSRF-Token"));
            Assert.False(request.Headers.Contains("X-Request-Nonce"));
            switch (requestIndex)
            {
                case 1:
                    {
                        Assert.Equal(HttpMethod.Post, request.Method);
                        Assert.Equal("https://mods.example/api/v1/uploads", request.RequestUri?.AbsoluteUri);
                        Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
                        using var body = JsonDocument.Parse(
                            await request.Content!.ReadAsStringAsync(cancellationToken));
                        Assert.Equal("translations.zip", body.RootElement.GetProperty("filename").GetString());
                        Assert.Equal(5, body.RootElement.GetProperty("size").GetInt64());
                        Assert.Equal(new string('a', 64), body.RootElement.GetProperty("sha256").GetString());
                        return JsonResponse(
                            UploadSessionJson(uploadId),
                            HttpStatusCode.Created);
                    }

                case 2:
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.Equal(
                        $"https://mods.example/api/v1/uploads/{uploadId:D}",
                        request.RequestUri?.AbsoluteUri);
                    return JsonResponse(UploadSessionJson(uploadId));

                case 3:
                    {
                        Assert.Equal(HttpMethod.Put, request.Method);
                        Assert.Equal(
                            $"https://mods.example/api/v1/uploads/{uploadId:D}/chunks/1",
                            request.RequestUri?.AbsoluteUri);
                        Assert.Equal(new string('b', 64), Assert.Single(request.Headers.GetValues("X-Chunk-SHA256")));
                        Assert.Equal("bytes 4-4/5", request.Content?.Headers.ContentRange?.ToString());
                        Assert.Equal("application/octet-stream", request.Content?.Headers.ContentType?.MediaType);
                        Assert.Equal(1, request.Content?.Headers.ContentLength);
                        Assert.Equal(
                            new byte[] { 5 },
                            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    }

                case 4:
                    {
                        Assert.Equal(HttpMethod.Post, request.Method);
                        Assert.Equal(
                            $"https://mods.example/api/v1/uploads/{uploadId:D}/complete",
                            request.RequestUri?.AbsoluteUri);
                        using var body = JsonDocument.Parse(
                            await request.Content!.ReadAsStringAsync(cancellationToken));
                        Assert.Equal("Translation Pack", body.RootElement.GetProperty("title").GetString());
                        Assert.Equal("1.0.0", body.RootElement.GetProperty("version_name").GetString());
                        Assert.Equal(tagId, body.RootElement.GetProperty("tag_ids")[0].GetGuid());
                        Assert.False(body.RootElement.GetProperty("is_official").GetBoolean());
                        return JsonResponse(
                            $$"""
                        {
                          "mod_id": "{{modId}}",
                          "version_id": "{{versionId}}",
                          "status": "pending_review",
                          "sha256": "{{new string('a', 64)}}",
                          "size": 5
                        }
                        """,
                            HttpStatusCode.Created);
                    }

                case 5:
                    Assert.Equal(HttpMethod.Delete, request.Method);
                    Assert.Equal(
                        $"https://mods.example/api/v1/uploads/{uploadId:D}",
                        request.RequestUri?.AbsoluteUri);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);

                default:
                    throw new InvalidOperationException("Unexpected Mod platform request.");
            }
        });
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_upload-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);

        var created = await client.CreateUploadAsync(
            new ModPlatformCreateUploadRequest(" translations.zip ", 5, new string('A', 64)),
            TestContext.Current.CancellationToken);
        var status = await client.GetUploadAsync(created.Id, TestContext.Current.CancellationToken);
        await using var finalChunk = new MemoryStream(new byte[] { 5, 6 });
        await client.UploadChunkAsync(
            status,
            1,
            finalChunk,
            new string('B', 64),
            TestContext.Current.CancellationToken);
        var completed = await client.CompleteUploadAsync(
            uploadId,
            new ModPlatformCompleteUploadRequest(
                null,
                "Translation Pack",
                "LocaleSmith translation resources",
                "A reviewed translation resource archive.",
                "1.0.0",
                ["1.21.1"],
                ["fabric"],
                [tagId],
                Publish: true),
            TestContext.Current.CancellationToken);
        await client.AbortUploadAsync(uploadId, TestContext.Current.CancellationToken);

        Assert.Equal(uploadId, status.Id);
        Assert.Equal(modId, completed.ModId);
        Assert.Equal(versionId, completed.VersionId);
        Assert.Equal("pending_review", completed.Status);
        Assert.Equal(5, requestIndex);
    }

    [Fact]
    public async Task UploadWorkflowRequiresExactSuccessStatusCodes()
    {
        var uploadId = Guid.NewGuid();
        var modId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var requestIndex = 0;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            requestIndex++;
            return Task.FromResult(requestIndex switch
            {
                1 => JsonResponse(UploadSessionJson(uploadId), HttpStatusCode.OK),
                2 => JsonResponse(UploadSessionJson(uploadId), HttpStatusCode.Created),
                3 => new HttpResponseMessage(HttpStatusCode.OK),
                4 => JsonResponse(
                    $$"""
                    {
                      "mod_id": "{{modId}}",
                      "version_id": "{{versionId}}",
                      "status": "pending_review",
                      "sha256": "{{new string('a', 64)}}",
                      "size": 5
                    }
                    """,
                    HttpStatusCode.OK),
                5 => new HttpResponseMessage(HttpStatusCode.OK),
                _ => throw new InvalidOperationException("Unexpected Mod platform request.")
            });
        });
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_upload-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);
        var upload = new ModPlatformUploadSession(
            uploadId,
            "translations.zip",
            5,
            4,
            2,
            new string('a', 64),
            "uploading",
            [0],
            DateTimeOffset.UtcNow.AddHours(1));
        var completeRequest = new ModPlatformCompleteUploadRequest(
            null,
            "Translation Pack",
            "LocaleSmith resources",
            "A reviewed translation resource archive.",
            "1.0.0",
            ["1.21.1"],
            ["fabric"],
            [tagId]);

        var createError = await Assert.ThrowsAsync<ModPlatformException>(() => client.CreateUploadAsync(
            new ModPlatformCreateUploadRequest("translations.zip", 5, new string('a', 64)),
            TestContext.Current.CancellationToken));
        var getError = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.GetUploadAsync(uploadId, TestContext.Current.CancellationToken));
        await using var chunk = new MemoryStream([5]);
        var chunkError = await Assert.ThrowsAsync<ModPlatformException>(() => client.UploadChunkAsync(
            upload,
            1,
            chunk,
            new string('b', 64),
            TestContext.Current.CancellationToken));
        var completeError = await Assert.ThrowsAsync<ModPlatformException>(() => client.CompleteUploadAsync(
            uploadId,
            completeRequest,
            TestContext.Current.CancellationToken));
        var abortError = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.AbortUploadAsync(uploadId, TestContext.Current.CancellationToken));

        Assert.All(
            [createError, getError, chunkError, completeError, abortError],
            error => Assert.Equal("invalid_response", error.Code));
        Assert.Equal(5, requestIndex);
    }

    [Fact]
    public async Task RejectsUploadMetadataOutsideWebsiteContractBeforeNetwork()
    {
        var invoked = false;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            invoked = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));
        var uploadId = Guid.NewGuid();
        var valid = new ModPlatformCompleteUploadRequest(
            null,
            "Translation Pack",
            "LocaleSmith resources",
            "A reviewed translation resource archive.",
            "1.0.0",
            ["1.21.1"],
            ["fabric"],
            [Guid.NewGuid()]);

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateUploadAsync(
            new ModPlatformCreateUploadRequest(
                $"{new string('x', 177)}.zip",
                5,
                new string('a', 64)),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CompleteUploadAsync(
            uploadId,
            valid with { Title = "ab" },
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CompleteUploadAsync(
            uploadId,
            valid with { Description = "<script>" },
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CompleteUploadAsync(
            uploadId,
            valid with { GameVersions = [] },
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CompleteUploadAsync(
            uploadId,
            valid with { Loaders = ["fabric\n"] },
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CompleteUploadAsync(
            uploadId,
            valid with { TagIds = [] },
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CompleteUploadAsync(
            uploadId,
            valid with { TagIds = [valid.TagIds[0], valid.TagIds[0]] },
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CompleteUploadAsync(
            uploadId,
            valid with { Changelog = new string('x', 20_001) },
            TestContext.Current.CancellationToken));

        Assert.False(invoked);
    }

    [Fact]
    public async Task RejectsUploadSessionWhoseChunkCountDoesNotMatchFixedLayout()
    {
        var invoked = false;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            invoked = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));
        var upload = new ModPlatformUploadSession(
            Guid.NewGuid(),
            "translations.zip",
            5,
            4,
            3,
            new string('a', 64),
            "uploading",
            [],
            DateTimeOffset.UtcNow.AddHours(1));
        await using var chunk = new MemoryStream([1, 2, 3, 4]);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.UploadChunkAsync(
                upload,
                0,
                chunk,
                new string('b', 64),
                TestContext.Current.CancellationToken));

        Assert.False(invoked);
    }

    [Fact]
    public async Task UploadChunksUseDedicatedTransferClient()
    {
        var apiRequests = 0;
        var transferRequests = 0;
        using var apiHandler = new StubHttpMessageHandler((_, _) =>
        {
            apiRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });
        using var transferHandler = new StubHttpMessageHandler((request, _) =>
        {
            transferRequests++;
            Assert.Equal(HttpMethod.Put, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var apiHttpClient = new HttpClient(apiHandler)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        using var transferHttpClient = new HttpClient(transferHandler)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_upload-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(
            apiHttpClient,
            transferHttpClient,
            new Uri("https://mods.example/"),
            provider);
        var upload = new ModPlatformUploadSession(
            Guid.NewGuid(),
            "translations.zip",
            4,
            4,
            1,
            new string('a', 64),
            "uploading",
            [],
            DateTimeOffset.UtcNow.AddHours(1));
        await using var chunk = new MemoryStream([1, 2, 3, 4]);

        await client.UploadChunkAsync(
            upload,
            0,
            chunk,
            new string('b', 64),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, apiRequests);
        Assert.Equal(1, transferRequests);
        Assert.Equal(TimeSpan.FromSeconds(45), apiHttpClient.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(30), transferHttpClient.Timeout);
    }

    [Fact]
    public async Task CompleteUploadUsesDedicatedTransferClient()
    {
        var uploadId = Guid.NewGuid();
        var modId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var apiRequests = 0;
        var transferRequests = 0;
        using var apiHandler = new StubHttpMessageHandler((_, _) =>
        {
            apiRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });
        using var transferHandler = new StubHttpMessageHandler((request, _) =>
        {
            transferRequests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                $"https://mods.example/api/v1/uploads/{uploadId:D}/complete",
                request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer mctx_pat_upload-secret", request.Headers.Authorization?.ToString());
            return Task.FromResult(JsonResponse(
                $$"""
                {
                  "mod_id": "{{modId}}",
                  "version_id": "{{versionId}}",
                  "status": "pending_review",
                  "sha256": "{{new string('a', 64)}}",
                  "size": 5
                }
                """,
                HttpStatusCode.Created));
        });
        using var apiHttpClient = new HttpClient(apiHandler)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        using var transferHttpClient = new HttpClient(transferHandler)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_upload-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(
            apiHttpClient,
            transferHttpClient,
            new Uri("https://mods.example/"),
            provider);

        var completed = await client.CompleteUploadAsync(
            uploadId,
            new ModPlatformCompleteUploadRequest(
                null,
                "Translation Pack",
                "LocaleSmith resources",
                "A reviewed translation resource archive.",
                "1.0.0",
                ["1.21.1"],
                ["fabric"],
                [Guid.NewGuid()]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, apiRequests);
        Assert.Equal(1, transferRequests);
        Assert.Equal(modId, completed.ModId);
        Assert.Equal(versionId, completed.VersionId);
    }

    [Fact]
    public void FactoryKeepsReadTimeoutShortAndProvidesLongTransferTimeout()
    {
        using var requestClient = ModPlatformHttpClientFactory.Create();
        using var transferClient = ModPlatformHttpClientFactory.CreateForTransfer();

        Assert.Equal(TimeSpan.FromSeconds(45), requestClient.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(30), transferClient.Timeout);
    }

    [Fact]
    public async Task CredentialServiceStoresAndDeletesPatThroughSecretStore()
    {
        using var secrets = new InMemorySecretStore();
        var credentials = new SecretStoreModPlatformCredentialService(secrets);

        Assert.False(await credentials.IsConfiguredAsync(TestContext.Current.CancellationToken));

        await credentials.SaveAsync(
            "mctx_pat_test-secret".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.True(await credentials.IsConfiguredAsync(TestContext.Current.CancellationToken));
        using var saved = await secrets.ResolveAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            TestContext.Current.CancellationToken);
        Assert.NotNull(saved);
        Assert.Equal("mctx_pat_test-secret", saved.DangerousGetString());

        Assert.True(await credentials.DeleteAsync(TestContext.Current.CancellationToken));
        Assert.False(await credentials.IsConfiguredAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null, null, "https://api.dzxh-tx.cn/")]
    [InlineData("Development", "http://127.0.0.1:8080/", "http://127.0.0.1:8080/")]
    public void EndpointPolicyAllowsOnlyExplicitLoopbackDevelopmentOverride(
        string? environment,
        string? configured,
        string expected)
    {
        Assert.Equal(expected, ModPlatformEndpointPolicy.Resolve(environment, configured).AbsoluteUri);
    }

    [Theory]
    [InlineData("Production", "http://127.0.0.1:8080/")]
    [InlineData("Development", "https://attacker.example/")]
    [InlineData("Development", "http://127.0.0.1:8080/api/")]
    public void EndpointPolicyRejectsUncontrolledOverride(string environment, string configured) =>
        Assert.Throws<InvalidOperationException>(() => ModPlatformEndpointPolicy.Resolve(environment, configured));

    [Fact]
    public async Task MapsStableErrorEnvelope()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"error":{"code":"not_found","message":"不存在"}}""",
                Encoding.UTF8,
                "application/json")
        }));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.GetModAsync("missing", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal("not_found", exception.Code);
        Assert.Equal("不存在", exception.Message);
    }

    [Fact]
    public async Task ConvertsHttpClientTimeoutIntoStableModPlatformError()
    {
        using var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        });
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(20)
        };
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.GetMetaAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.RequestTimeout, exception.StatusCode);
        Assert.Equal("request_timeout", exception.Code);
        Assert.Equal("The Mod platform request timed out.", exception.Message);
    }

    [Fact]
    public async Task PreservesCallerCancellation()
    {
        using var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetMetaAsync(cancellation.Token));
    }

    [Fact]
    public async Task ConvertsTransportFailureIntoStableNetworkError()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("Host-specific details must not escape."));
        using var httpClient = new HttpClient(handler);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"));

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => client.GetMetaAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal("network_error", exception.Code);
        Assert.Equal("The Mod platform network request failed.", exception.Message);
    }

    [Theory]
    [InlineData("page")]
    [InlineData("tag")]
    [InlineData("mod_summary")]
    [InlineData("mod_detail")]
    [InlineData("thread")]
    [InlineData("post")]
    [InlineData("upload")]
    [InlineData("completed_upload")]
    public async Task RejectsMissingRequiredFieldsAcrossResponseContracts(string contract)
    {
        var json = contract switch
        {
            "page" => "{}",
            "tag" => "[{}]",
            "mod_summary" => """{"data":[{}],"page":1,"page_size":20,"total":1}""",
            "mod_detail" => "{}",
            "thread" => "{}",
            "post" => """{"data":[{}],"page":1,"page_size":30,"total":1}""",
            "upload" => "{}",
            "completed_upload" => "{}",
            _ => throw new InvalidOperationException("Unknown response contract test case.")
        };
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(json)));
        using var httpClient = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            "mctx_pat_contract-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var provider = new SecretStoreModPlatformAccessTokenProvider(secrets);
        using var client = new ModPlatformClient(httpClient, new Uri("https://mods.example/"), provider);
        var id = Guid.NewGuid();
        Func<Task> action = contract switch
        {
            "page" => () => client.GetModsAsync(cancellationToken: TestContext.Current.CancellationToken),
            "tag" => () => client.GetTagsAsync(TestContext.Current.CancellationToken),
            "mod_summary" => () => client.GetModsAsync(cancellationToken: TestContext.Current.CancellationToken),
            "mod_detail" => () => client.GetModAsync(id.ToString("D"), TestContext.Current.CancellationToken),
            "thread" => () => client.GetThreadAsync(id, TestContext.Current.CancellationToken),
            "post" => () => client.GetPostsAsync(id, cancellationToken: TestContext.Current.CancellationToken),
            "upload" => () => client.GetUploadAsync(id, TestContext.Current.CancellationToken),
            "completed_upload" => () => client.CompleteUploadAsync(
                id,
                new ModPlatformCompleteUploadRequest(
                    null,
                    "Translation Pack",
                    "LocaleSmith resources",
                    "A reviewed translation resource archive.",
                    "1.0.0",
                    ["1.21.1"],
                    ["fabric"],
                    [Guid.NewGuid()]),
                TestContext.Current.CancellationToken),
            _ => throw new InvalidOperationException("Unknown response contract test case.")
        };

        var exception = await Assert.ThrowsAsync<ModPlatformException>(action);

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task ArtifactDownloaderRequiresApiVersionHeaderOnVersionedHeadResponse()
    {
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            addApiVersionHeader: false);
        using var httpClient = new HttpClient(handler);
        using var downloader = new ModPlatformArtifactDownloader(httpClient, new Uri("https://mods.example/"));
        using var temporary = new TemporaryDirectory();
        var destination = Path.Combine(temporary.Path, "demo.jar");
        var version = CreateVersion(1, new string('0', 64), "/api/v1/files/download-id/download");

        var exception = await Assert.ThrowsAsync<ModPlatformException>(
            () => downloader.DownloadAsync(
                version,
                destination,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task DownloaderResumesWithRangeAndVerifiesSha256()
    {
        var payload = "hello world"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        const string entityTag = "\"nginx-immutable-v1\"";
        var version = CreateVersion(payload.Length, sha256, "/api/v1/files/download-id/download");
        using var temporary = new TemporaryDirectory();
        var destination = Path.Combine(temporary.Path, "demo.jar");
        var partial = ModPlatformArtifactDownloader.GetPartialPath(destination);
        var metadata = ModPlatformArtifactDownloader.GetMetadataPath(destination);
        await File.WriteAllBytesAsync(partial, payload[..4], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            metadata,
            $$"""{"sha256":"{{sha256}}","size":{{payload.Length}},"etag":"{{entityTag.Replace("\"", "\\\"")}}"}""",
            TestContext.Current.CancellationToken);
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
                head.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(entityTag);
                head.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(head);
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            var range = Assert.Single(request.Headers.Range!.Ranges);
            Assert.Equal(4, range.From);
            Assert.Null(range.To);
            Assert.Equal(entityTag, request.Headers.IfRange?.EntityTag?.Tag);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[4..])
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(entityTag);
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                4,
                payload.Length - 1,
                payload.Length);
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        using var downloader = new ModPlatformArtifactDownloader(httpClient, new Uri("https://mods.example/"));

        await downloader.DownloadAsync(version, destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(metadata));
    }

    [Fact]
    public async Task DownloaderRejectsCrossOriginArtifactUrlBeforeNetworkRequest()
    {
        var invoked = false;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            invoked = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var httpClient = new HttpClient(handler);
        using var downloader = new ModPlatformArtifactDownloader(httpClient, new Uri("https://mods.example/"));
        using var temporary = new TemporaryDirectory();
        var destination = Path.Combine(temporary.Path, "demo.jar");
        var version = CreateVersion(
            0,
            new string('0', 64),
            "https://attacker.example/payload.jar");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.DownloadAsync(
                version,
                destination,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(invoked);
    }

    private static ModPlatformVersion CreateVersion(long size, string sha256, string downloadUrl) => new(
        Guid.NewGuid(),
        "1.0.0",
        ["1.21.1"],
        ["fabric"],
        string.Empty,
        "demo.jar",
        size,
        sha256,
        0,
        DateTimeOffset.Parse("2026-08-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        downloadUrl);

    private static string ValidMinimalMetaJson() =>
        """
        {
          "service": "MCTX Mod Hub",
          "build_id": "test",
          "supported_api_majors": [1],
          "preferred_api_major": 1,
          "features": ["personal_access_token_v1", "forum_v1"],
          "limits": {
            "max_mod_bytes": 2147483648,
            "upload_chunk_bytes": 8388608,
            "upload_concurrency": 3,
            "download_range_concurrency": 4
          },
          "turnstile": {"required": false, "site_key": null},
          "server_time": "2026-08-15T00:00:00Z"
        }
        """;

    private static string TagJson(Guid tagId) =>
        $$"""
        {
          "id": "{{tagId}}",
          "slug": "official",
          "name": "官方",
          "description": "由平台管理员发布并维护的官方 Mod。",
          "color": "#0A84FF",
          "is_official": true
        }
        """;

    private static string VersionJson(Guid versionId) =>
        $$"""
        {
          "id": "{{versionId}}",
          "version_name": "1.0.0",
          "game_versions": ["1.21.1"],
          "loaders": ["fabric"],
          "changelog": "Initial release",
          "filename": "translations.zip",
          "size": 5,
          "sha256": "{{new string('a', 64)}}",
          "downloads": 0,
          "created_at": "2026-08-15T00:00:00Z",
          "download_url": "/api/v1/files/download-id/download"
        }
        """;

    private static string ModSummaryJson(Guid modId, string tagJson, string versionJson) =>
        $$"""
        {
          "id": "{{modId}}",
          "slug": "translation-pack",
          "title": "Translation Pack",
          "summary": "LocaleSmith translation resources",
          "status": "published",
          "is_official": true,
          "owner_id": "50000000-0000-0000-0000-000000000001",
          "owner_name": "LocaleSmith",
          "downloads": 0,
          "updated_at": "2026-08-15T00:00:00Z",
          "published_at": "2026-08-15T00:00:00Z",
          "tags": [{{tagJson}}],
          "latest_version": {{versionJson}}
        }
        """;

    private static string ModDetailJson(Guid modId, string tagJson, string versionJson) =>
        $$"""
        {
          "id": "{{modId}}",
          "slug": "translation-pack",
          "title": "Translation Pack",
          "summary": "LocaleSmith translation resources",
          "status": "published",
          "is_official": true,
          "owner_id": "50000000-0000-0000-0000-000000000001",
          "owner_name": "LocaleSmith",
          "downloads": 0,
          "updated_at": "2026-08-15T00:00:00Z",
          "published_at": "2026-08-15T00:00:00Z",
          "tags": [{{tagJson}}],
          "latest_version": {{versionJson}},
          "description": "A reviewed translation resource archive.",
          "versions": [{{versionJson}}],
          "permissions": {
            "can_edit": false,
            "can_delete": false,
            "can_moderate": false
          }
        }
        """;

    private static string ThreadJson(Guid threadId, Guid modId, Guid authorId) =>
        $$"""
        {
          "id": "{{threadId}}",
          "mod_id": "{{modId}}",
          "title": "Compatibility discussion",
          "author_id": "{{authorId}}",
          "author_name": "alex",
          "reply_count": 1,
          "status": "open",
          "locked": false,
          "created_at": "2026-08-15T00:00:00Z",
          "updated_at": "2026-08-15T00:00:00Z"
        }
        """;

    private static string PostJson(Guid postId, Guid threadId, Guid authorId) =>
        $$"""
        {
          "id": "{{postId}}",
          "thread_id": "{{threadId}}",
          "author_id": "{{authorId}}",
          "author_name": "alex",
          "content_markdown": "Useful reply",
          "created_at": "2026-08-15T00:00:00Z",
          "updated_at": "2026-08-15T00:00:00Z"
        }
        """;

    private static string UploadSessionJson(Guid uploadId) =>
        $$"""
        {
          "id": "{{uploadId}}",
          "filename": "translations.zip",
          "size": 5,
          "chunk_size": 4,
          "total_chunks": 2,
          "expected_sha256": "{{new string('a', 64)}}",
          "status": "uploading",
          "uploaded_chunks": [0],
          "expires_at": "2026-08-16T00:00:00Z"
        }
        """;

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback,
        bool addApiVersionHeader = true) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await callback(request, cancellationToken);
            if (addApiVersionHeader
                && request.RequestUri?.AbsolutePath.StartsWith("/api/v1/", StringComparison.Ordinal) == true
                && !response.Headers.Contains(ModPlatformApiContract.ApiVersionHeaderName))
            {
                response.Headers.TryAddWithoutValidation(
                    ModPlatformApiContract.ApiVersionHeaderName,
                    ModPlatformApiContract.ApiVersion);
            }

            return response;
        }
    }
}
