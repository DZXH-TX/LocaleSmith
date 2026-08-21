using System.Net.Http;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.Presentation.Tests;

public sealed class CommunityViewModelTests
{
    [Fact]
    public async Task HierarchyPromptsRemainMutuallyExclusive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var mod = CreateMod(1);
        var thread = CreateThread(mod.Id, 1);

        using (var emptyMods = CreateViewModel(new FakeModPlatformClient()))
        {
            await emptyMods.InitializeAsync(cancellationToken);

            Assert.True(emptyMods.ShowModsEmptyState);
            Assert.False(emptyMods.ShowThreadsEmptyState);
            Assert.False(emptyMods.ShowSelectThreadPrompt);
        }

        using (var emptyThreads = CreateViewModel(new FakeModPlatformClient
        {
            GetModsHandler = (_, _) => Task.FromResult(Page([mod], 1))
        }))
        {
            await emptyThreads.InitializeAsync(cancellationToken);
            await emptyThreads.SelectModAsync(mod, cancellationToken);

            Assert.False(emptyThreads.ShowModsEmptyState);
            Assert.True(emptyThreads.ShowThreadsEmptyState);
            Assert.False(emptyThreads.ShowSelectThreadPrompt);
        }

        using var availableThread = CreateViewModel(new FakeModPlatformClient
        {
            GetModsHandler = (_, _) => Task.FromResult(Page([mod], 1)),
            GetThreadsHandler = (_, _, _, _) => Task.FromResult(Page([thread], 1))
        });
        await availableThread.InitializeAsync(cancellationToken);
        await availableThread.SelectModAsync(mod, cancellationToken);

        Assert.False(availableThread.ShowModsEmptyState);
        Assert.False(availableThread.ShowThreadsEmptyState);
        Assert.True(availableThread.ShowSelectThreadPrompt);

        await availableThread.SelectThreadAsync(thread, cancellationToken);

        Assert.False(availableThread.ShowSelectThreadPrompt);
    }

    [Fact]
    public async Task TimeoutIsCapturedAsRetryableErrorAndDoesNotShowEmptyState()
    {
        var calls = 0;
        var client = new FakeModPlatformClient
        {
            GetModsHandler = (_, _) =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<ModPlatformPage<ModPlatformModSummary>>(
                        new TaskCanceledException("request timeout"))
                    : Task.FromResult(Page<ModPlatformModSummary>([]));
            }
        };
        using var viewModel = CreateViewModel(client);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.IsInitialized);
        Assert.True(viewModel.HasError);
        Assert.Contains("timed out", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.ShowModsEmptyState);
        Assert.True(viewModel.RetryCommand.CanExecute(null));

        await viewModel.RetryCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsInitialized);
        Assert.False(viewModel.HasError);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ModSelectionRetryReloadsTheAlreadySelectedMod()
    {
        var mod = CreateMod(1);
        var thread = CreateThread(mod.Id, 1);
        var calls = 0;
        var client = new FakeModPlatformClient
        {
            GetModsHandler = (_, _) => Task.FromResult(Page([mod], 1)),
            GetThreadsHandler = (_, _, _, _) =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<ModPlatformPage<ModPlatformForumThread>>(
                        new HttpRequestException("offline"))
                    : Task.FromResult(Page([thread], 1));
            }
        };
        using var viewModel = CreateViewModel(client);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.SelectModAsync(mod, TestContext.Current.CancellationToken);
        Assert.True(viewModel.HasError);

        await viewModel.RetryCommand.ExecuteAsync(null);

        Assert.Equal(2, calls);
        Assert.Equal(thread.Id, Assert.Single(viewModel.Threads).Id);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task ThreadSelectionRetryReloadsTheAlreadySelectedThread()
    {
        var mod = CreateMod(1);
        var thread = CreateThread(mod.Id, 1);
        var post = CreatePost(thread.Id, 1);
        var calls = 0;
        var client = new FakeModPlatformClient
        {
            GetModsHandler = (_, _) => Task.FromResult(Page([mod], 1)),
            GetThreadsHandler = (_, _, _, _) => Task.FromResult(Page([thread], 1)),
            GetPostsHandler = (_, _, _, _) =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<ModPlatformPage<ModPlatformForumPost>>(
                        new HttpRequestException("offline"))
                    : Task.FromResult(Page([post], 1));
            }
        };
        using var viewModel = CreateViewModel(client);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        await viewModel.SelectModAsync(mod, TestContext.Current.CancellationToken);

        await viewModel.SelectThreadAsync(thread, TestContext.Current.CancellationToken);
        Assert.True(viewModel.HasError);

        await viewModel.RetryCommand.ExecuteAsync(null);

        Assert.Equal(2, calls);
        Assert.Equal(post.Id, Assert.Single(viewModel.Posts).Id);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task CancelledInitializationCanBeStartedAgainWithoutSuccessStatus()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var client = new FakeModPlatformClient
        {
            GetModsHandler = async (_, cancellationToken) =>
            {
                calls++;
                if (calls == 1)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return Page<ModPlatformModSummary>([]);
            }
        };
        using var viewModel = CreateViewModel(client);
        using var cancellation = new CancellationTokenSource();

        var firstAttempt = viewModel.InitializeAsync(cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await firstAttempt;

        Assert.False(viewModel.IsInitialized);
        Assert.False(viewModel.HasStatus);
        Assert.False(viewModel.HasError);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsInitialized);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SupersededModRequestCannotOverwriteTheNewSelection()
    {
        var firstMod = CreateMod(1);
        var secondMod = CreateMod(2);
        var firstThread = CreateThread(firstMod.Id, 1);
        var secondThread = CreateThread(secondMod.Id, 2);
        var firstRequestEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeModPlatformClient
        {
            GetModsHandler = (_, _) => Task.FromResult(Page([firstMod, secondMod], 2)),
            GetThreadsHandler = async (modId, _, _, _) =>
            {
                if (modId == firstMod.Id)
                {
                    firstRequestEntered.TrySetResult();
                    await releaseFirstRequest.Task;
                    return Page([firstThread], 1);
                }

                return Page([secondThread], 1);
            }
        };
        using var viewModel = CreateViewModel(client);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        var firstSelection = viewModel.SelectModAsync(
            firstMod,
            TestContext.Current.CancellationToken);
        await firstRequestEntered.Task;
        await viewModel.SelectModAsync(secondMod, TestContext.Current.CancellationToken);
        releaseFirstRequest.TrySetResult();
        await firstSelection;

        Assert.Equal(secondMod.Id, viewModel.SelectedMod?.Id);
        Assert.Equal(secondThread.Id, Assert.Single(viewModel.Threads).Id);
        Assert.False(viewModel.HasStatus);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task FailedThreadCreationPreservesDraftAndCannotBlindlyRetryPost()
    {
        var mod = CreateMod(1);
        var client = new FakeModPlatformClient
        {
            GetModsHandler = (_, _) => Task.FromResult(Page([mod], 1)),
            CreateThreadHandler = (_, _, _, _) =>
                Task.FromException<ModPlatformForumThread>(new HttpRequestException("offline"))
        };
        using var viewModel = CreateViewModel(client, isPatConfigured: true);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        await viewModel.SelectModAsync(mod, TestContext.Current.CancellationToken);
        viewModel.NewThreadTitle = "Valid topic";
        viewModel.NewThreadContent = "Draft content";

        await viewModel.CreateThreadCommand.ExecuteAsync(null);

        Assert.Equal("Valid topic", viewModel.NewThreadTitle);
        Assert.Equal("Draft content", viewModel.NewThreadContent);
        Assert.True(viewModel.HasError);
        Assert.False(viewModel.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedReplyPreservesDraftAndCannotBlindlyRetryPost()
    {
        var mod = CreateMod(1);
        var thread = CreateThread(mod.Id, 1);
        var client = new FakeModPlatformClient
        {
            GetModsHandler = (_, _) => Task.FromResult(Page([mod], 1)),
            GetThreadsHandler = (_, _, _, _) => Task.FromResult(Page([thread], 1)),
            CreatePostHandler = (_, _, _) =>
                Task.FromException<ModPlatformForumPost>(new HttpRequestException("offline"))
        };
        using var viewModel = CreateViewModel(client, isPatConfigured: true);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        await viewModel.SelectModAsync(mod, TestContext.Current.CancellationToken);
        await viewModel.SelectThreadAsync(thread, TestContext.Current.CancellationToken);
        viewModel.ReplyContent = "Draft reply";

        await viewModel.CreateReplyCommand.ExecuteAsync(null);

        Assert.Equal("Draft reply", viewModel.ReplyContent);
        Assert.True(viewModel.HasError);
        Assert.False(viewModel.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task SignInDisablesCredentialActionsAndIgnoresSecondSubmission()
    {
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var credentials = new FakeCredentialService(false)
        {
            SaveHandler = async (_, _) =>
            {
                saveEntered.TrySetResult();
                await releaseSave.Task;
            }
        };
        var client = new FakeModPlatformClient();
        using var viewModel = new CommunityViewModel(client, credentials);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        var firstSave = viewModel.SignInAsync(
            "First_User",
            "FirstPassword7".AsMemory(),
            "mctx_pat_first-token".AsMemory(),
            TestContext.Current.CancellationToken);
        await saveEntered.Task;

        Assert.False(viewModel.CanSavePat);
        Assert.False(viewModel.CanDeletePat);
        await viewModel.SignInAsync(
            "Second_User",
            "SecondPassword7".AsMemory(),
            "mctx_pat_second-token".AsMemory(),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, client.VerifyApplicationLoginCalls);
        Assert.Equal(1, credentials.SaveCalls);

        releaseSave.TrySetResult();
        await firstSave;

        Assert.False(viewModel.CanSavePat);
        Assert.True(viewModel.CanDeletePat);
    }

    [Theory]
    [InlineData("", "ValidPassword7", "mctx_pat_application-secret")]
    [InlineData("Test_User", "", "mctx_pat_application-secret")]
    [InlineData("Test_User", "ValidPassword7", "")]
    public async Task ApplicationLoginRequiresUsernamePasswordAndTokenBeforeCallingClient(
        string username,
        string password,
        string applicationToken)
    {
        var client = new FakeModPlatformClient();
        var credentials = new FakeCredentialService(false);
        using var viewModel = new CommunityViewModel(client, credentials);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.SignInAsync(
            username,
            password.AsMemory(),
            applicationToken.AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, client.VerifyApplicationLoginCalls);
        Assert.Equal(0, client.VerifyApplicationTokenCalls);
        Assert.Equal(0, credentials.SaveCalls);
        Assert.False(viewModel.IsAuthenticated);
        Assert.True(viewModel.HasError);
    }

    [Fact]
    public async Task ApplicationLoginVerifiesBeforeSavingOnlyTheApplicationToken()
    {
        const string password = "NeverPersistThisPassword7";
        const string applicationToken = "mctx_pat_application-secret";
        var session = CreateAuthSession(
            username: "Canonical_User",
            scopes: ["mods:read", "forum:write", "reports:write"]);
        var client = new FakeModPlatformClient
        {
            VerifyApplicationLoginHandler = (username, suppliedPassword, suppliedToken, _) =>
            {
                Assert.Equal("typed_user", username);
                Assert.Equal(password, suppliedPassword.ToString());
                Assert.Equal(applicationToken, suppliedToken.ToString());
                return Task.FromResult(session);
            }
        };
        var credentials = new FakeCredentialService(false);
        using var viewModel = new CommunityViewModel(client, credentials);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.SignInAsync(
            "  typed_user  ",
            password.AsMemory(),
            applicationToken.AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, client.VerifyApplicationLoginCalls);
        Assert.Equal(0, client.VerifyApplicationTokenCalls);
        Assert.Equal(1, credentials.SaveCalls);
        Assert.Equal(applicationToken, Assert.Single(credentials.SavedTokens));
        Assert.DoesNotContain(password, credentials.SavedTokens);
        Assert.True(viewModel.IsAuthenticated);
        Assert.Equal("Canonical_User", viewModel.CurrentUsername);
        Assert.True(viewModel.CanWriteForum);
        Assert.True(viewModel.HasReportPermission);
        Assert.Contains("password was not saved", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticatedCapabilitiesAreGatedByGrantedScopes()
    {
        var cases = new[]
        {
            (Scopes: Array.Empty<string>(), CanWrite: false, CanReport: false),
            (Scopes: new[] { "profile:read" }, CanWrite: false, CanReport: false),
            (Scopes: new[] { "mods:read", "forum:read" }, CanWrite: false, CanReport: false),
            (Scopes: new[] { "mods:read", "reports:write" }, CanWrite: false, CanReport: true),
            (Scopes: new[] { "mods:read", "forum:write" }, CanWrite: true, CanReport: true)
        };

        foreach (var testCase in cases)
        {
            var client = new FakeModPlatformClient
            {
                VerifyApplicationLoginHandler = (_, _, _, _) => Task.FromResult(
                    CreateAuthSession(scopes: testCase.Scopes))
            };
            using var viewModel = CreateViewModel(client);
            await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

            await viewModel.SignInAsync(
                "Test_User",
                "ValidPassword7".AsMemory(),
                "mctx_pat_scope-test".AsMemory(),
                TestContext.Current.CancellationToken);

            Assert.True(viewModel.IsAuthenticated);
            Assert.Equal(testCase.CanWrite, viewModel.CanWriteForum);
            Assert.Equal(testCase.CanReport, viewModel.HasReportPermission);
            Assert.Equal(testCase.CanReport, viewModel.CanReportContent);
        }
    }

    [Fact]
    public async Task LegacyServerVerifiesUsernameAndTokenWithoutSendingPassword()
    {
        const string password = "NeverSendThisLegacyPassword7";
        const string applicationToken = "mctx_pat_legacy-application";
        var meta = CreateMeta() with
        {
            Features = ["personal_access_token_v1", "forum_v1", "content_reports_v1"]
        };
        var client = new FakeModPlatformClient
        {
            GetMetaHandler = _ => Task.FromResult(meta),
            VerifyApplicationTokenHandler = (username, suppliedToken, _) =>
            {
                Assert.Equal("legacy_user", username);
                Assert.Equal(applicationToken, suppliedToken.ToString());
                return Task.FromResult(CreateAuthSession(username: "Legacy_User", scopes: []));
            }
        };
        var credentials = new FakeCredentialService(false);
        using var viewModel = new CommunityViewModel(client, credentials);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.SignInAsync(
            " legacy_user ",
            password.AsMemory(),
            applicationToken.AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.False(viewModel.SupportsApplicationLogin);
        Assert.Equal(0, client.VerifyApplicationLoginCalls);
        Assert.Equal(1, client.VerifyApplicationTokenCalls);
        Assert.Equal(applicationToken, Assert.Single(credentials.SavedTokens));
        Assert.DoesNotContain(password, credentials.SavedTokens);
        Assert.True(viewModel.IsAuthenticated);
        Assert.False(viewModel.CanWriteForum);
        Assert.False(viewModel.HasReportPermission);
        Assert.Contains("not sent or saved", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidStoredApplicationTokenIsDeletedDuringSessionRestore()
    {
        const string serverDiagnostic = "server-secret-invalid-token-diagnostic";
        var client = new FakeModPlatformClient
        {
            GetAuthenticatedSessionHandler = _ => Task.FromException<ModPlatformAuthSession>(
                new FakePlatformException("unauthorized", serverDiagnostic))
        };
        var credentials = new FakeCredentialService(true);
        using var viewModel = new CommunityViewModel(client, credentials);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsInitialized);
        Assert.Equal(1, client.GetAuthenticatedSessionCalls);
        Assert.Equal(1, credentials.DeleteCalls);
        Assert.False(credentials.IsConfigured);
        Assert.False(viewModel.IsPatConfigured);
        Assert.False(viewModel.IsAuthenticated);
        Assert.Null(viewModel.CurrentUser);
        Assert.Contains("invalid or expired", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(serverDiagnostic, viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkFailureDuringSessionRestorePreservesStoredApplicationToken()
    {
        const string serverDiagnostic = "server-secret-network-diagnostic";
        var client = new FakeModPlatformClient
        {
            GetAuthenticatedSessionHandler = _ => Task.FromException<ModPlatformAuthSession>(
                new HttpRequestException(serverDiagnostic))
        };
        var credentials = new FakeCredentialService(true);
        using var viewModel = new CommunityViewModel(client, credentials);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsInitialized);
        Assert.Equal(1, client.GetAuthenticatedSessionCalls);
        Assert.Equal(0, credentials.DeleteCalls);
        Assert.True(credentials.IsConfigured);
        Assert.True(viewModel.IsPatConfigured);
        Assert.False(viewModel.IsAuthenticated);
        Assert.True(viewModel.ShowSignInForm);
        Assert.Contains("could not be verified", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(serverDiagnostic, viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unauthorized", "incorrect")]
    [InlineData("invalid_credentials", "incorrect")]
    [InlineData("forbidden", "permitted")]
    [InlineData("rate_limited", "many")]
    [InlineData("request_timeout", "timed out")]
    [InlineData("network_error", "reached")]
    [InlineData("invalid_response", "unexpected")]
    [InlineData("unknown_server_code", "could not")]
    public async Task ApplicationLoginBranchesOnStableCodeWithoutDisplayingServerMessage(
        string code,
        string expectedText)
    {
        const string serverDiagnostic = "server-secret-login-diagnostic";
        var client = new FakeModPlatformClient
        {
            VerifyApplicationLoginHandler = (_, _, _, _) =>
                Task.FromException<ModPlatformAuthSession>(
                    new FakePlatformException(code, serverDiagnostic))
        };
        var credentials = new FakeCredentialService(false);
        using var viewModel = new CommunityViewModel(client, credentials);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.SignInAsync(
            "Test_User",
            "ValidPassword7".AsMemory(),
            "mctx_pat_application-secret".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.True(viewModel.HasError);
        Assert.Contains(expectedText, viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(serverDiagnostic, viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, credentials.SaveCalls);
        Assert.False(viewModel.IsAuthenticated);
        Assert.False(viewModel.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task ModsAndThreadsCanLoadPastTheFirstFiftyItems()
    {
        var mods = Enumerable.Range(1, 60).Select(CreateMod).ToArray();
        var threads = Enumerable.Range(1, 55).Select(index => CreateThread(mods[0].Id, index)).ToArray();
        var client = new FakeModPlatformClient
        {
            GetModsHandler = (options, _) => Task.FromResult(
                options?.Page == 2
                    ? Page(mods[50..], 60, page: 2)
                    : Page(mods[..50], 60)),
            GetThreadsHandler = (_, page, _, _) => Task.FromResult(
                page == 2
                    ? Page(threads[50..], 55, page: 2)
                    : Page(threads[..50], 55))
        };
        using var viewModel = CreateViewModel(client);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.HasMoreMods);
        await viewModel.LoadMoreModsCommand.ExecuteAsync(null);
        Assert.Equal(60, viewModel.Mods.Count);
        Assert.False(viewModel.HasMoreMods);

        await viewModel.SelectModAsync(mods[0], TestContext.Current.CancellationToken);
        Assert.True(viewModel.HasMoreThreads);
        await viewModel.LoadMoreThreadsCommand.ExecuteAsync(null);
        Assert.Equal(55, viewModel.Threads.Count);
        Assert.False(viewModel.HasMoreThreads);
    }

    [Fact]
    public async Task ReportValidatesReasonAndSubmitsOnlyOnceWithoutAutomaticRetry()
    {
        var post = CreatePost(Guid.NewGuid(), 1);
        ModPlatformReportRequest? submittedRequest = null;
        var client = new FakeModPlatformClient
        {
            CreateReportHandler = (request, _) =>
            {
                submittedRequest = request;
                return Task.FromResult(CreateReport(request));
            }
        };
        using var viewModel = CreateViewModel(client, isPatConfigured: true);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.ReportPostAsync(post, "bad", TestContext.Current.CancellationToken);
        Assert.True(viewModel.HasReportError);
        Assert.Null(submittedRequest);
        Assert.False(viewModel.RetryCommand.CanExecute(null));

        await viewModel.ReportPostAsync(
            post,
            "  abusive content  ",
            TestContext.Current.CancellationToken);

        Assert.NotNull(submittedRequest);
        Assert.Equal(ModPlatformReportTargetTypes.ForumPost, submittedRequest.TargetType);
        Assert.Equal(post.Id, submittedRequest.TargetId);
        Assert.Equal(ModPlatformReportCategories.Other, submittedRequest.Category);
        Assert.Equal("abusive content", submittedRequest.Details);
        Assert.False(viewModel.HasReportError);
        Assert.True(viewModel.HasStatus);
        Assert.False(viewModel.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReportingMetaControlsPolicyLinksCategoriesAndTargets()
    {
        using var viewModel = CreateViewModel(new FakeModPlatformClient());

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsReportFeatureAvailable);
        Assert.Equal("https://dow.dzxh-tx.cn/terms", viewModel.TermsUri.AbsoluteUri);
        Assert.Equal(
            "https://dow.dzxh-tx.cn/community-guidelines",
            viewModel.CommunityGuidelinesUri.AbsoluteUri);
        Assert.Equal(12, viewModel.ReportCategories.Count);
        Assert.Contains(viewModel.ReportCategories, option => option.Code == "child_safety");
        foreach (var targetType in ModPlatformReportTargetTypes.All)
        {
            Assert.True(viewModel.SupportsReportTarget(targetType));
        }
    }

    [Fact]
    public async Task ReportingMetaIgnoresUnknownExtensionsAndKeepsSupportedIntersectionAvailable()
    {
        var meta = CreateMeta();
        meta = meta with
        {
            Reporting = meta.Reporting! with
            {
                TargetTypes = [ModPlatformReportTargetTypes.Mod, "collection"],
                Categories = [ModPlatformReportCategories.Other, "misinformation"]
            }
        };
        var client = new FakeModPlatformClient
        {
            GetMetaHandler = _ => Task.FromResult(meta)
        };
        using var viewModel = CreateViewModel(client);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsInitialized);
        Assert.True(viewModel.IsReportFeatureAvailable);
        Assert.True(viewModel.SupportsReportTarget(ModPlatformReportTargetTypes.Mod));
        Assert.False(viewModel.SupportsReportTarget(ModPlatformReportTargetTypes.ForumPost));
        var category = Assert.Single(viewModel.ReportCategories);
        Assert.Equal(ModPlatformReportCategories.Other, category.Code);
    }

    [Fact]
    public async Task CanonicalReportCoversEveryVisibleUgcTargetType()
    {
        var submitted = new List<ModPlatformReportRequest>();
        var client = new FakeModPlatformClient
        {
            CreateReportHandler = (request, _) =>
            {
                submitted.Add(request);
                return Task.FromResult(CreateReport(request));
            }
        };
        using var viewModel = CreateViewModel(client, isPatConfigured: true);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        foreach (var targetType in ModPlatformReportTargetTypes.All)
        {
            var target = new CommunityReportTarget(targetType, Guid.NewGuid(), $"Target {targetType}");
            var result = await viewModel.SubmitReportAsync(
                target,
                ModPlatformReportCategories.Other,
                "  Moderators should review this content.  ",
                TestContext.Current.CancellationToken);

            Assert.True(result);
        }

        Assert.Equal(ModPlatformReportTargetTypes.All.Count, submitted.Count);
        Assert.Equal(
            ModPlatformReportTargetTypes.All.Order(StringComparer.Ordinal),
            submitted.Select(request => request.TargetType).Order(StringComparer.Ordinal));
        Assert.All(
            submitted,
            request => Assert.Equal("Moderators should review this content.", request.Details));
    }

    [Fact]
    public async Task ReportWithoutPatShowsRecoveryGuidanceWithoutCallingApi()
    {
        var calls = 0;
        var client = new FakeModPlatformClient
        {
            CreateReportHandler = (request, _) =>
            {
                calls++;
                return Task.FromResult(CreateReport(request));
            }
        };
        using var viewModel = CreateViewModel(client);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        var submitted = await viewModel.SubmitReportAsync(
            new CommunityReportTarget("forum_post", Guid.NewGuid(), "Post"),
            "spam",
            "Repeated advertisement",
            TestContext.Current.CancellationToken);

        Assert.False(submitted);
        Assert.Equal(0, calls);
        Assert.Contains("token", viewModel.ReportErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reports:write", viewModel.ReportErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("already_reported", "already")]
    [InlineData("forbidden", "reports:write")]
    [InlineData("unauthorized", "invalid")]
    [InlineData("not_found", "no longer")]
    [InlineData("invalid_report_target_type", "no longer")]
    [InlineData("invalid_report_category", "category")]
    [InlineData("invalid_report_details", "1,900")]
    [InlineData("rate_limited", "many")]
    [InlineData("security_service_unavailable", "security")]
    [InlineData("report_duplicate", "could not")]
    [InlineData("unknown_server_code", "could not")]
    public async Task ReportBranchesOnStableCodeWithoutDisplayingServerMessage(
        string code,
        string expectedText)
    {
        var client = new FakeModPlatformClient
        {
            CreateReportHandler = (_, _) => Task.FromException<ModPlatformReport>(
                new FakePlatformException(code, "server-secret-diagnostic"))
        };
        using var viewModel = CreateViewModel(client, isPatConfigured: true);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        var submitted = await viewModel.SubmitReportAsync(
            new CommunityReportTarget("mod", Guid.NewGuid(), "Mod"),
            "other",
            "Moderators should review this content.",
            TestContext.Current.CancellationToken);

        Assert.False(submitted);
        Assert.Contains(expectedText, viewModel.ReportErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("server-secret-diagnostic", viewModel.ReportErrorMessage, StringComparison.Ordinal);
    }

    private static CommunityViewModel CreateViewModel(
        FakeModPlatformClient client,
        bool isPatConfigured = false) =>
        new(client, new FakeCredentialService(isPatConfigured));

    private static ModPlatformPage<T> Page<T>(
        IReadOnlyList<T> data,
        long total = 0,
        long page = 1) =>
        new(data, page, 50, total);

    private static ModPlatformMeta CreateMeta() => new(
        "MCTX Mod Hub",
        "test",
        [1],
        1,
        ["personal_access_token_v1", "application_login_v1", "forum_v1", "content_reports_v1"],
        new ModPlatformLimits(2_147_483_648, 8_388_608, 3, 4),
        new ModPlatformTurnstile(false, null),
        DateTimeOffset.UnixEpoch,
        Reporting: new ModPlatformReportingCapabilities(
            "https://dow.dzxh-tx.cn/terms",
            "https://dow.dzxh-tx.cn/community-guidelines",
            [.. ModPlatformReportTargetTypes.All],
            [.. ModPlatformReportCategories.All]));

    private static ModPlatformReport CreateReport(ModPlatformReportRequest request) => new(
        Guid.NewGuid(),
        request.TargetType,
        request.TargetId,
        request.Category,
        "open",
        DateTimeOffset.UnixEpoch);

    private static ModPlatformAuthSession CreateAuthSession(
        string username = "Test_User",
        IReadOnlyList<string>? scopes = null) =>
        new(
            new ModPlatformUser(
                Guid.Parse("60000000-0000-0000-0000-000000000001"),
                username,
                "user"),
            DateTimeOffset.Parse(
                "2026-08-16T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            scopes ?? ["mods:read", "forum:write", "reports:write"]);

    private static ModPlatformModSummary CreateMod(int index) =>
        new(
            Guid.Parse($"00000000-0000-0000-0000-{index:D12}"),
            $"mod-{index}",
            $"Mod {index}",
            "Summary",
            "published",
            false,
            "Owner",
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            [],
            null,
            Guid.Parse("50000000-0000-0000-0000-000000000001"));

    private static ModPlatformForumThread CreateThread(Guid modId, int index) =>
        new(
            Guid.Parse($"10000000-0000-0000-0000-{index:D12}"),
            modId,
            $"Thread {index}",
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            "Author",
            0,
            "open",
            false,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static ModPlatformForumPost CreatePost(Guid threadId, int index) =>
        new(
            Guid.Parse($"30000000-0000-0000-0000-{index:D12}"),
            threadId,
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            "Author",
            "Content",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private sealed class FakeCredentialService(bool isConfigured) : IModPlatformCredentialService
    {
        public Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> SaveHandler { get; init; } =
            static (_, _) => ValueTask.CompletedTask;

        public int SaveCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public bool IsConfigured => isConfigured;

        public List<string> SavedTokens { get; } = [];

        public ValueTask<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(isConfigured);

        public async ValueTask SaveAsync(
            ReadOnlyMemory<char> token,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            SavedTokens.Add(token.ToString());
            await SaveHandler(token, cancellationToken);
            isConfigured = true;
        }

        public ValueTask<bool> DeleteAsync(CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            var deleted = isConfigured;
            isConfigured = false;
            return ValueTask.FromResult(deleted);
        }
    }

    private sealed class FakeModPlatformClient : IModPlatformClient
    {
        public Func<CancellationToken, Task<ModPlatformMeta>> GetMetaHandler { get; init; } =
            static _ => Task.FromResult(CreateMeta());

        public Func<
            string,
            ReadOnlyMemory<char>,
            ReadOnlyMemory<char>,
            CancellationToken,
            Task<ModPlatformAuthSession>> VerifyApplicationLoginHandler
        { get; init; } = static (_, _, _, _) => Task.FromResult(CreateAuthSession());

        public Func<
            string,
            ReadOnlyMemory<char>,
            CancellationToken,
            Task<ModPlatformAuthSession>> VerifyApplicationTokenHandler
        { get; init; } = static (_, _, _) => Task.FromResult(CreateAuthSession());

        public Func<CancellationToken, Task<ModPlatformAuthSession>> GetAuthenticatedSessionHandler
        { get; init; } = static _ => Task.FromResult(CreateAuthSession());

        public int VerifyApplicationLoginCalls { get; private set; }

        public int VerifyApplicationTokenCalls { get; private set; }

        public int GetAuthenticatedSessionCalls { get; private set; }

        public Func<ModPlatformSearchOptions?, CancellationToken, Task<ModPlatformPage<ModPlatformModSummary>>>
            GetModsHandler
        { get; init; } = static (_, _) =>
                Task.FromResult(Page<ModPlatformModSummary>([]));

        public Func<Guid, int, int, CancellationToken, Task<ModPlatformPage<ModPlatformForumThread>>>
            GetThreadsHandler
        { get; init; } = static (_, _, _, _) =>
                Task.FromResult(Page<ModPlatformForumThread>([]));

        public Func<Guid, int, int, CancellationToken, Task<ModPlatformPage<ModPlatformForumPost>>>
            GetPostsHandler
        { get; init; } = static (_, _, _, _) =>
                Task.FromResult(Page<ModPlatformForumPost>([]));

        public Func<Guid, string, string, CancellationToken, Task<ModPlatformForumThread>>
            CreateThreadHandler
        { get; init; } = static (_, _, _, _) =>
                Task.FromException<ModPlatformForumThread>(new NotSupportedException());

        public Func<Guid, string, CancellationToken, Task<ModPlatformForumPost>>
            CreatePostHandler
        { get; init; } = static (_, _, _) =>
                Task.FromException<ModPlatformForumPost>(new NotSupportedException());

        public Func<ModPlatformReportRequest, CancellationToken, Task<ModPlatformReport>>
            CreateReportHandler
        { get; init; } = static (request, _) => Task.FromResult(CreateReport(request));

        public Task<ModPlatformMeta> GetMetaAsync(CancellationToken cancellationToken = default) =>
            GetMetaHandler(cancellationToken);

        public Task<ModPlatformAuthSession> VerifyApplicationLoginAsync(
            string username,
            ReadOnlyMemory<char> password,
            ReadOnlyMemory<char> applicationToken,
            CancellationToken cancellationToken = default)
        {
            VerifyApplicationLoginCalls++;
            return VerifyApplicationLoginHandler(
                username,
                password,
                applicationToken,
                cancellationToken);
        }

        public Task<ModPlatformAuthSession> VerifyApplicationTokenAsync(
            string username,
            ReadOnlyMemory<char> applicationToken,
            CancellationToken cancellationToken = default)
        {
            VerifyApplicationTokenCalls++;
            return VerifyApplicationTokenHandler(username, applicationToken, cancellationToken);
        }

        public Task<ModPlatformAuthSession> GetAuthenticatedSessionAsync(
            CancellationToken cancellationToken = default)
        {
            GetAuthenticatedSessionCalls++;
            return GetAuthenticatedSessionHandler(cancellationToken);
        }

        public Task<ModPlatformPage<ModPlatformModSummary>> GetModsAsync(
            ModPlatformSearchOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetModsHandler(options, cancellationToken);

        public Task<ModPlatformModDetail> GetModAsync(
            string idOrSlug,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ModPlatformTag>> GetTagsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ModPlatformUploadSession> CreateUploadAsync(
            ModPlatformCreateUploadRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ModPlatformUploadSession> GetUploadAsync(
            Guid uploadId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UploadChunkAsync(
            ModPlatformUploadSession upload,
            int chunkIndex,
            Stream content,
            string chunkSha256,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ModPlatformCompletedUpload> CompleteUploadAsync(
            Guid uploadId,
            ModPlatformCompleteUploadRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AbortUploadAsync(
            Guid uploadId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ModPlatformPage<ModPlatformForumThread>> GetThreadsAsync(
            Guid modId,
            int page = 1,
            int pageSize = 30,
            CancellationToken cancellationToken = default) =>
            GetThreadsHandler(modId, page, pageSize, cancellationToken);

        public Task<ModPlatformForumThread> GetThreadAsync(
            Guid threadId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ModPlatformPage<ModPlatformForumPost>> GetPostsAsync(
            Guid threadId,
            int page = 1,
            int pageSize = 30,
            CancellationToken cancellationToken = default) =>
            GetPostsHandler(threadId, page, pageSize, cancellationToken);

        public Task<ModPlatformForumThread> CreateThreadAsync(
            Guid modId,
            string title,
            string content,
            CancellationToken cancellationToken = default) =>
            CreateThreadHandler(modId, title, content, cancellationToken);

        public Task<ModPlatformForumPost> CreatePostAsync(
            Guid threadId,
            string content,
            CancellationToken cancellationToken = default) =>
            CreatePostHandler(threadId, content, cancellationToken);

        public Task<ModPlatformReport> CreateReportAsync(
            ModPlatformReportRequest request,
            CancellationToken cancellationToken = default) =>
            CreateReportHandler(request, cancellationToken);

        public Task ReportPostAsync(
            Guid postId,
            string reason,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakePlatformException(string code, string message) :
        Exception(message),
        IModPlatformServiceError
    {
        public string Code { get; } = code;
    }
}
