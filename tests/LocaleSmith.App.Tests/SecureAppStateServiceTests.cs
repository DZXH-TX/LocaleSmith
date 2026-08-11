using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocaleSmith.App.Services;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Tests;

public sealed class SecureAppStateServiceTests
{
    private const string CredentialReference = "model-sources/cloud/api-key";
    private const string OriginalSecret = "original-super-secret";

    [Fact]
    public async Task InitializeRestoresPersistedSourcesAndPublishesSelectedSnapshot()
    {
        var events = new ConcurrentQueue<string>();
        var profile = OllamaProfile() with
        {
            DisplayName = "Saved local model",
            ModelName = "qwen2.5:14b"
        };
        var configurationStore = new RecordingConfigurationStore(CreateConfiguration(profile), events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var registry = new ModelServiceRegistry();
        using var httpClient = new HttpClient(new RejectingHttpHandler());
        using var service = new SecureAppStateService(
            configurationStore,
            secretStore,
            registry,
            httpClient,
            new StubSandboxRootManager());
        ModelSelectionStateChangedEventArgs? published = null;
        service.StateChanged += (_, args) => published = args;

        await service.InitializeAsync(TestContext.Current.CancellationToken);

        var restored = Assert.Single(service.Sources);
        Assert.Equal("ollama", restored.Id);
        Assert.Equal("Saved local model", restored.DisplayName);
        Assert.Equal("qwen2.5:14b", restored.ModelName);
        Assert.Equal("ollama", service.SelectedSource?.Id);
        Assert.NotNull(published);
        Assert.Equal("ollama", Assert.Single(published.Sources).Id);
        Assert.Equal("ollama", published.SelectedSource?.Id);
    }

    [Fact]
    public async Task ConcurrentInitializationNormalizesLegacyDefaultPathsAndPersistsExactlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var documentsRoot = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.MyDocuments);
        Assert.False(string.IsNullOrWhiteSpace(documentsRoot));
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(
            new AppConfiguration
            {
                IsOnboardingComplete = true,
                WorkspacePath = Path.Combine(documentsRoot, "JaxI18n"),
                SandboxPath = Path.Combine(Path.GetTempPath(), "JaxI18n", "Sandbox"),
                LogDirectoryPath = string.Empty
            },
            events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var registry = new ModelServiceRegistry();
        using var httpClient = new HttpClient(new RejectingHttpHandler());
        using var service = new SecureAppStateService(
            configurationStore,
            secretStore,
            registry,
            httpClient,
            new StubSandboxRootManager());

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.InitializeAsync(cancellationToken)));
        var loaded = await service.LoadAsync(cancellationToken);

        Assert.Equal(Path.Combine(documentsRoot, "LocaleSmith"), loaded.WorkspacePath);
        Assert.Equal(AppConfiguration.GetDefaultSandboxPath(), loaded.SandboxPath);
        Assert.Equal(AppConfiguration.GetDefaultLogDirectoryPath(), loaded.LogDirectoryPath);
        Assert.Equal(loaded.WorkspacePath, configurationStore.Persisted.WorkspacePath);
        Assert.Equal(loaded.SandboxPath, configurationStore.Persisted.SandboxPath);
        Assert.Equal(loaded.LogDirectoryPath, configurationStore.Persisted.LogDirectoryPath);
        Assert.Equal(1, events.Count(static entry => entry == "configuration:save"));
    }

    [Fact]
    public async Task InitializationMigratesPreviousLocaleSmithTemporarySandboxDefault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(
            new AppConfiguration
            {
                SandboxPath = Path.Combine(Path.GetTempPath(), "LocaleSmith", "Sandbox")
            },
            events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var registry = new ModelServiceRegistry();
        using var httpClient = new HttpClient(new RejectingHttpHandler());
        using var service = new SecureAppStateService(
            configurationStore,
            secretStore,
            registry,
            httpClient,
            new StubSandboxRootManager());

        await service.InitializeAsync(cancellationToken);
        var loaded = await service.LoadAsync(cancellationToken);

        Assert.Equal(AppConfiguration.GetDefaultSandboxPath(), loaded.SandboxPath);
        Assert.Equal(loaded.SandboxPath, configurationStore.Persisted.SandboxPath);
        Assert.Equal(1, events.Count(static entry => entry == "configuration:save"));
    }

    [Fact]
    public async Task InitializationPersistsLegacyPresetDefaultsBeforeBuildingRuntimeRegistry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var events = new ConcurrentQueue<string>();
        var legacyProfile = CloudProfile() with
        {
            PresetId = ModelProviderPresets.MiniMaxId,
            TokenLimitParameter = null,
            Endpoint = "http://127.0.0.1:11434",
            ModelName = "llama3"
        };
        var existingLogDirectory = Path.Combine(
            AppContext.BaseDirectory,
            ".test-artifacts",
            "existing-translation-logs");
        var configurationStore = new RecordingConfigurationStore(
            CreateConfiguration(legacyProfile) with
            {
                SchemaVersion = 2,
                LogDirectoryPath = existingLogDirectory
            },
            events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var registry = new ModelServiceRegistry();
        using var httpClient = new HttpClient(new RejectingHttpHandler());
        using var service = new SecureAppStateService(
            configurationStore,
            secretStore,
            registry,
            httpClient,
            new StubSandboxRootManager());

        await service.InitializeAsync(cancellationToken);

        var persisted = Assert.Single(configurationStore.Persisted.ModelSources);
        Assert.Equal("https://api.minimax.io/v1", persisted.Endpoint.TrimEnd('/'));
        Assert.Equal("MiniMax-M2.7", persisted.ModelName);
        Assert.Equal(OpenAiTokenLimitParameter.MaxCompletionTokens, service.SelectedSource?.TokenLimitParameter);
        Assert.Equal("https://api.minimax.io/v1", service.SelectedSource?.Endpoint.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("MiniMax-M2.7", service.SelectedSource?.ModelName);
        Assert.Equal(existingLogDirectory, configurationStore.Persisted.LogDirectoryPath);
        Assert.Equal(1, events.Count(static entry => entry == "configuration:save"));
    }

    [Fact]
    public async Task SettingsSaveCanRetryBootstrapProjectionAfterItsFirstWriteFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directories = new TemporaryTestDirectory();
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(
            new AppConfiguration
            {
                IsOnboardingComplete = true,
                WorkspacePath = directories.WorkspacePath,
                SandboxPath = directories.SandboxPath,
                LogDirectoryPath = directories.LogDirectoryPath
            },
            events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var registry = new ModelServiceRegistry();
        using var httpClient = new HttpClient(new RejectingHttpHandler());
        var languageWriter = new FailOnceLanguagePreferenceWriter();
        using var service = new SecureAppStateService(
            configurationStore,
            secretStore,
            registry,
            httpClient,
            new StubSandboxRootManager(),
            languageWriter);
        var update = new AppSettingsUpdate(
            AppDisplayLanguages.EnglishUnitedStates,
            AppThemePreference.System,
            false,
            directories.WorkspacePath,
            directories.SandboxPath,
            directories.LogDirectoryPath);

        await Assert.ThrowsAsync<IOException>(() =>
            service.SaveSettingsAsync(update, cancellationToken));

        Assert.Equal(AppDisplayLanguages.EnglishUnitedStates, configurationStore.Persisted.Language);
        Assert.Equal(AppDisplayLanguages.EnglishUnitedStates, (await service.LoadAsync(cancellationToken)).Language);
        Assert.Equal(1, languageWriter.Attempts);
        Assert.Null(languageWriter.PersistedLanguage);

        await service.SaveSettingsAsync(update, cancellationToken);

        Assert.Equal(2, languageWriter.Attempts);
        Assert.Equal(AppDisplayLanguages.EnglishUnitedStates, languageWriter.PersistedLanguage);
    }

    [Fact]
    public async Task InitializationPreservesCurrentSchemaEditablePresetValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var events = new ConcurrentQueue<string>();
        var editedProfile = CloudProfile() with
        {
            PresetId = ModelProviderPresets.MiniMaxId,
            Endpoint = "http://127.0.0.1:11434",
            ModelName = "llama3"
        };
        var configurationStore = new RecordingConfigurationStore(
            CreateConfiguration(editedProfile),
            events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var registry = new ModelServiceRegistry();
        using var httpClient = new HttpClient(new RejectingHttpHandler());
        using var service = new SecureAppStateService(
            configurationStore,
            secretStore,
            registry,
            httpClient,
            new StubSandboxRootManager());

        await service.InitializeAsync(cancellationToken);

        Assert.Equal("http://127.0.0.1:11434", service.SelectedSource?.Endpoint.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("llama3", service.SelectedSource?.ModelName);
        Assert.Empty(events);
    }

    [Fact]
    public async Task InitializationDoesNotRewriteEditableCustomPresetDefaults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var events = new ConcurrentQueue<string>();
        var customProfile = CloudProfile() with
        {
            PresetId = ModelProviderPresets.CustomId,
            Endpoint = "http://127.0.0.1:11434",
            ModelName = "llama3"
        };
        var configurationStore = new RecordingConfigurationStore(
            CreateConfiguration(customProfile),
            events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var registry = new ModelServiceRegistry();
        using var httpClient = new HttpClient(new RejectingHttpHandler());
        using var service = new SecureAppStateService(
            configurationStore,
            secretStore,
            registry,
            httpClient,
            new StubSandboxRootManager());

        await service.InitializeAsync(cancellationToken);

        Assert.Equal("http://127.0.0.1:11434", service.SelectedSource?.Endpoint.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("llama3", service.SelectedSource?.ModelName);
        Assert.Empty(events);
    }

    [Fact]
    public async Task InitializationPreservesCustomPathsContainingLegacyProductNameWithoutSaving()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var customRoot = Path.Combine(Path.GetTempPath(), $"custom-{Guid.NewGuid():N}");
        var customWorkspace = Path.Combine(customRoot, "JaxI18n");
        var customSandbox = Path.Combine(customRoot, "JaxI18n", "Sandbox");
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(
            new AppConfiguration
            {
                IsOnboardingComplete = true,
                WorkspacePath = customWorkspace,
                SandboxPath = customSandbox
            },
            events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var registry = new ModelServiceRegistry();
        using var httpClient = new HttpClient(new RejectingHttpHandler());
        using var service = new SecureAppStateService(
            configurationStore,
            secretStore,
            registry,
            httpClient,
            new StubSandboxRootManager());

        await service.InitializeAsync(cancellationToken);
        var loaded = await service.LoadAsync(cancellationToken);

        Assert.Equal(customWorkspace, loaded.WorkspacePath);
        Assert.Equal(customSandbox, loaded.SandboxPath);
        Assert.Empty(events);
    }

    [Fact]
    public async Task ModelSelectionNotificationCanSynchronouslyReenterStateService()
    {
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(
            CreateConfiguration(OllamaProfile()),
            events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var registry = new ModelServiceRegistry();
        using var httpClient = new HttpClient(new RejectingHttpHandler());
        using var service = new SecureAppStateService(
            configurationStore,
            secretStore,
            registry,
            httpClient,
            new StubSandboxRootManager());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        AppConfiguration? reloaded = null;
        service.StateChanged += (_, _) =>
            reloaded = service.LoadAsync(timeout.Token).GetAwaiter().GetResult();

        await service.InitializeAsync(timeout.Token);

        Assert.NotNull(reloaded);
        Assert.Equal("ollama", reloaded.SelectedModelSourceId);
    }

    [Fact]
    public async Task LegacyCustomProfileWithoutTokenParameterUsesMaxTokensAtRuntime()
    {
        var events = new ConcurrentQueue<string>();
        var profile = CloudProfile();
        Assert.Null(profile.TokenLimitParameter);
        var configurationStore = new RecordingConfigurationStore(CreateConfiguration(profile), events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var harness = await CreateHarnessAsync(configurationStore, secretStore);

        var runtimeSource = Assert.Single(harness.Registry.Sources);
        Assert.Equal(ModelProviderPresets.CustomId, runtimeSource.PresetId);
        Assert.Equal(OpenAiTokenLimitParameter.MaxTokens, runtimeSource.TokenLimitParameter);
    }

    [Fact]
    public async Task InitializeRejectsUnknownPersistedTokenParameter()
    {
        var events = new ConcurrentQueue<string>();
        var invalid = CloudProfile() with
        {
            TokenLimitParameter = (OpenAiTokenLimitParameter)999
        };
        var configurationStore = new RecordingConfigurationStore(CreateConfiguration(invalid), events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var registry = new ModelServiceRegistry();
        using var httpClient = new HttpClient(new RejectingHttpHandler());
        using var service = new SecureAppStateService(
            configurationStore,
            secretStore,
            registry,
            httpClient,
            new StubSandboxRootManager());

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal("tokenLimitParameter", exception.ParamName);
        Assert.Empty(registry.Sources);
        Assert.Empty(events);
    }

    [Fact]
    public async Task DeleteRemovesCredentialBeforeCommittingConfiguration()
    {
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(CreateConfiguration(CloudProfile()), events);
        using var secretStore = new FaultInjectingSecretStore(events);
        secretStore.Seed(CredentialReference, OriginalSecret);
        using var harness = await CreateHarnessAsync(configurationStore, secretStore);
        events.Clear();

        var deleted = await harness.Service.DeleteAsync("cloud", TestContext.Current.CancellationToken);

        Assert.True(deleted);
        Assert.Equal(
            [
                $"secret:resolve:{CredentialReference}",
                $"secret:delete:{CredentialReference}",
                "configuration:save"
            ],
            events.ToArray());
        Assert.Empty(configurationStore.Persisted.ModelSources);
        Assert.Null(secretStore.GetSecretForTest(CredentialReference));
        Assert.False(harness.Registry.TryGet("cloud", out _));
    }

    [Fact]
    public async Task DeleteRestoresCredentialWhenConfigurationCommitFails()
    {
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(CreateConfiguration(CloudProfile()), events)
        {
            NextSaveException = new IOException("configuration write failed")
        };
        using var secretStore = new FaultInjectingSecretStore(events);
        secretStore.Seed(CredentialReference, OriginalSecret);
        using var harness = await CreateHarnessAsync(configurationStore, secretStore);
        events.Clear();

        var exception = await Assert.ThrowsAsync<IOException>(
            () => harness.Service.DeleteAsync("cloud", TestContext.Current.CancellationToken));

        Assert.Equal("configuration write failed", exception.Message);
        Assert.Equal(
            [
                $"secret:resolve:{CredentialReference}",
                $"secret:delete:{CredentialReference}",
                "configuration:save",
                $"secret:set:{CredentialReference}"
            ],
            events.ToArray());
        Assert.Equal(OriginalSecret, secretStore.GetSecretForTest(CredentialReference));
        Assert.Single(configurationStore.Persisted.ModelSources);
        Assert.True(harness.Registry.TryGet("cloud", out _));
        Assert.Single((await harness.Service.LoadAsync(TestContext.Current.CancellationToken)).ModelSources);
    }

    [Fact]
    public async Task DeleteReportsBothCommitAndCompensationFailuresWithoutSecretPlaintext()
    {
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(CreateConfiguration(CloudProfile()), events)
        {
            NextSaveException = new IOException("configuration write failed")
        };
        using var secretStore = new FaultInjectingSecretStore(events);
        secretStore.Seed(CredentialReference, OriginalSecret);
        secretStore.EnqueueSetFailure(new InvalidOperationException("credential store unavailable"));
        using var harness = await CreateHarnessAsync(configurationStore, secretStore);
        events.Clear();

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => harness.Service.DeleteAsync("cloud", TestContext.Current.CancellationToken));

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.IsType<IOException>(exception.InnerExceptions[0]);
        Assert.Contains(CredentialReference, exception.InnerExceptions[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(OriginalSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.Single(configurationStore.Persisted.ModelSources);
        Assert.True(harness.Registry.TryGet("cloud", out _));
        Assert.Null(secretStore.GetSecretForTest(CredentialReference));
    }

    [Fact]
    public async Task DeleteFailureAttemptsIdempotentCredentialRestoreBeforeReturning()
    {
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(CreateConfiguration(CloudProfile()), events);
        using var secretStore = new FaultInjectingSecretStore(events);
        secretStore.Seed(CredentialReference, OriginalSecret);
        secretStore.EnqueueDeleteFailure(new IOException("credential deletion failed"));
        using var harness = await CreateHarnessAsync(configurationStore, secretStore);
        events.Clear();

        await Assert.ThrowsAsync<IOException>(
            () => harness.Service.DeleteAsync("cloud", TestContext.Current.CancellationToken));

        Assert.Equal(
            [
                $"secret:resolve:{CredentialReference}",
                $"secret:delete:{CredentialReference}",
                $"secret:set:{CredentialReference}"
            ],
            events.ToArray());
        Assert.Equal(OriginalSecret, secretStore.GetSecretForTest(CredentialReference));
        Assert.Single(configurationStore.Persisted.ModelSources);
        Assert.True(harness.Registry.TryGet("cloud", out _));
    }

    [Fact]
    public async Task DeletingOllamaDoesNotTouchCredentialStore()
    {
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(CreateConfiguration(OllamaProfile()), events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var harness = await CreateHarnessAsync(configurationStore, secretStore);
        events.Clear();

        Assert.True(await harness.Service.DeleteAsync("ollama", TestContext.Current.CancellationToken));

        Assert.Equal(["configuration:save"], events.ToArray());
        Assert.Empty(configurationStore.Persisted.ModelSources);
    }

    [Fact]
    public async Task ReplacingCredentialRestoresPreviousValueWhenConfigurationCommitFails()
    {
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(CreateConfiguration(CloudProfile()), events)
        {
            NextSaveException = new IOException("configuration write failed")
        };
        using var secretStore = new FaultInjectingSecretStore(events);
        secretStore.Seed(CredentialReference, OriginalSecret);
        using var harness = await CreateHarnessAsync(configurationStore, secretStore);
        events.Clear();
        var draft = new ModelSourceDraft(
            "cloud",
            "Updated cloud",
            ModelProviderKind.OpenAiCompatible,
            new Uri("https://models.example.test/v1/"),
            "updated-model",
            CredentialReference);

        await Assert.ThrowsAsync<IOException>(
            () => harness.Service.SaveAsync(draft, "replacement-secret".AsMemory(), TestContext.Current.CancellationToken));

        Assert.Equal(
            [
                $"secret:resolve:{CredentialReference}",
                $"secret:set:{CredentialReference}",
                "configuration:save",
                $"secret:set:{CredentialReference}"
            ],
            events.ToArray());
        Assert.Equal(OriginalSecret, secretStore.GetSecretForTest(CredentialReference));
        Assert.Equal("Cloud", configurationStore.Persisted.ModelSources.Single().DisplayName);
        Assert.Equal("Cloud", harness.Registry.Sources.Single().DisplayName);
    }

    [Fact]
    public async Task SwitchingCloudSourceToOllamaRestoresRemovedCredentialOnCommitFailure()
    {
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(CreateConfiguration(CloudProfile()), events)
        {
            NextSaveException = new IOException("configuration write failed")
        };
        using var secretStore = new FaultInjectingSecretStore(events);
        secretStore.Seed(CredentialReference, OriginalSecret);
        using var harness = await CreateHarnessAsync(configurationStore, secretStore);
        events.Clear();
        var draft = new ModelSourceDraft(
            "cloud",
            "Local replacement",
            ModelProviderKind.Ollama,
            new Uri("http://127.0.0.1:11434/"),
            "llama3",
            CredentialReference);

        await Assert.ThrowsAsync<IOException>(
            () => harness.Service.SaveAsync(draft, ReadOnlyMemory<char>.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(OriginalSecret, secretStore.GetSecretForTest(CredentialReference));
        Assert.Equal("Cloud", configurationStore.Persisted.ModelSources.Single().DisplayName);
        Assert.Equal("Cloud", harness.Registry.Sources.Single().DisplayName);
    }

    [Fact]
    public async Task DeleteAndSaveTransactionsAreSerializedByServiceGate()
    {
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(CreateConfiguration(CloudProfile()), events);
        using var secretStore = new FaultInjectingSecretStore(events);
        secretStore.Seed(CredentialReference, OriginalSecret);
        using var harness = await CreateHarnessAsync(configurationStore, secretStore);
        events.Clear();
        configurationStore.BlockNextSave();

        var deleteTask = harness.Service.DeleteAsync("cloud", TestContext.Current.CancellationToken);
        await configurationStore.SaveEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var saveTask = harness.Service.SaveAsync(
            new ModelSourceDraft(
                "second",
                "Second",
                ModelProviderKind.OpenAiCompatible,
                new Uri("https://second.example.test/v1/"),
                "second-model",
                null),
            "second-secret".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.False(saveTask.IsCompleted);
        Assert.DoesNotContain(
            events,
            entry => entry == "secret:set:model-sources/second/api-key");

        configurationStore.ReleaseSave();
        Assert.True(await deleteTask);
        var saved = await saveTask;

        Assert.Equal("second", saved.Id);
        Assert.Equal(["second"], configurationStore.Persisted.ModelSources.Select(static source => source.Id));
        Assert.Equal("second-secret", secretStore.GetSecretForTest("model-sources/second/api-key"));
    }

    [Fact]
    public async Task DeepSeekSourceSavesReferenceOnlyThenResolvesStoredKeyBeforeConnectionRequest()
    {
        const string apiKey = "deepseek-secret-from-store";
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(new AppConfiguration(), events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var handler = new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            events.Enqueue("http:send");
            Assert.Equal("https://api.deepseek.com/v1/chat/completions", request.RequestUri?.AbsoluteUri);
            Assert.Equal($"Bearer {apiKey}", request.Headers.Authorization?.ToString());
            var requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("\"model\":\"deepseek-chat\"", requestBody, StringComparison.Ordinal);
            Assert.DoesNotContain("\"temperature\"", requestBody, StringComparison.Ordinal);
            Assert.Contains("\"max_tokens\":64", requestBody, StringComparison.Ordinal);
            Assert.DoesNotContain("\"max_completion_tokens\"", requestBody, StringComparison.Ordinal);
            return JsonResponse("""{"model":"deepseek-chat","choices":[{"message":{"content":"OK"}}]}""");
        });
        using var harness = await CreateHarnessAsync(configurationStore, secretStore, handler);
        var draft = new ModelSourceDraft(
            "deepseek",
            "DeepSeek",
            ModelProviderKind.OpenAiCompatible,
            new Uri("https://api.deepseek.com/v1/chat/completions"),
            "deepseek-chat",
            null,
            ModelProviderPresets.DeepSeekId);

        var saved = await harness.Service.SaveAsync(
            draft,
            apiKey.AsMemory(),
            TestContext.Current.CancellationToken);

        var persistedJson = JsonSerializer.Serialize(configurationStore.Persisted);
        Assert.DoesNotContain(apiKey, persistedJson, StringComparison.Ordinal);
        Assert.Equal("model-sources/deepseek/api-key", saved.CredentialReference);
        Assert.Equal(OpenAiTokenLimitParameter.MaxTokens, saved.TokenLimitParameter);
        Assert.Equal(apiKey, secretStore.GetSecretForTest(saved.CredentialReference!));
        events.Clear();

        var result = await harness.Service.TestConnectionAsync(
            draft with { CredentialReference = saved.CredentialReference },
            ReadOnlyMemory<char>.Empty,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccessful, result.Message);
        Assert.Equal(
            [$"secret:resolve:{saved.CredentialReference}", "http:send"],
            events.ToArray());
    }

    [Fact]
    public async Task ConnectionTestHonorsExplicitTokenLimitOmission()
    {
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(new AppConfiguration(), events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var handler = new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            var requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain("\"max_tokens\"", requestBody, StringComparison.Ordinal);
            Assert.DoesNotContain("\"max_completion_tokens\"", requestBody, StringComparison.Ordinal);
            return JsonResponse("""{"choices":[{"message":{"content":"OK"}}]}""");
        });
        using var harness = await CreateHarnessAsync(configurationStore, secretStore, handler);

        var result = await harness.Service.TestConnectionAsync(
            new ModelSourceDraft(
                "custom",
                "Custom",
                ModelProviderKind.OpenAiCompatible,
                new Uri("https://models.example.test/v1"),
                "custom-model",
                null,
                ModelProviderPresets.CustomId,
                OpenAiTokenLimitParameter.Omit),
            "temporary-secret".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccessful, result.Message);
    }

    [Fact]
    public async Task SaveRejectsUnknownDraftTokenParameterBeforeSecretMutation()
    {
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(new AppConfiguration(), events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var harness = await CreateHarnessAsync(configurationStore, secretStore);
        events.Clear();

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            harness.Service.SaveAsync(
                new ModelSourceDraft(
                    "invalid-token",
                    "Invalid token",
                    ModelProviderKind.OpenAiCompatible,
                    new Uri("https://models.example.test/v1"),
                    "model",
                    null,
                    ModelProviderPresets.CustomId,
                    (OpenAiTokenLimitParameter)999),
                "temporary-secret".AsMemory(),
                TestContext.Current.CancellationToken));

        Assert.Equal("parameter", exception.ParamName);
        Assert.Empty(events);
        Assert.Empty(configurationStore.Persisted.ModelSources);
    }

    [Fact]
    public async Task DeepSeekConnectionFailureReturnsSanitizedProviderDiagnostics()
    {
        const string apiKey = "stored-deepseek-secret";
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(CreateConfiguration(CloudProfile()), events);
        using var secretStore = new FaultInjectingSecretStore(events);
        secretStore.Seed(CredentialReference, apiKey);
        using var handler = new DelegateHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"Authentication failed for " + apiKey +
                    "\",\"api_key\":\"" + apiKey + "\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
            response.Headers.Add("x-request-id", "deepseek-request-401");
            return Task.FromResult(response);
        });
        using var harness = await CreateHarnessAsync(configurationStore, secretStore, handler);
        var profile = CloudProfile();

        var result = await harness.Service.TestConnectionAsync(
            new ModelSourceDraft(
                profile.Id,
                profile.DisplayName,
                profile.Provider,
                new Uri(profile.Endpoint),
                profile.ModelName,
                profile.CredentialReference),
            ReadOnlyMemory<char>.Empty,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Contains("HTTP 401", result.Message, StringComparison.Ordinal);
        Assert.Contains("Authentication failed", result.Message, StringComparison.Ordinal);
        Assert.Contains("deepseek-request-401", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnboardingNetworkPresetStoresCredentialBeforeReferenceOnlyConfiguration()
    {
        const string apiKey = "onboarding-network-secret";
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(new AppConfiguration(), events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var harness = await CreateHarnessAsync(configurationStore, secretStore);
        using var directories = new TemporaryTestDirectory();
        events.Clear();

        await harness.Service.CompleteAsync(
            new OnboardingSubmission(
                directories.WorkspacePath,
                directories.SandboxPath,
                ConfigureOllama: false,
                new Uri("http://127.0.0.1:11434"),
                "llama3",
                ModelProviderPresets.DeepSeekId,
                new Uri("https://api.deepseek.com"),
                "account-specific-deepseek-model",
                apiKey.AsMemory(),
                OpenAiTokenLimitParameter.MaxCompletionTokens,
                directories.LogDirectoryPath),
            TestContext.Current.CancellationToken);

        var profile = Assert.Single(configurationStore.Persisted.ModelSources);
        Assert.Equal("preset-deepseek", profile.Id);
        Assert.Equal(ModelProviderPresets.DeepSeekId, profile.PresetId);
        Assert.Equal("https://api.deepseek.com/", profile.Endpoint);
        Assert.Equal("account-specific-deepseek-model", profile.ModelName);
        Assert.Equal(OpenAiTokenLimitParameter.MaxCompletionTokens, profile.TokenLimitParameter);
        Assert.Equal("model-sources/preset-deepseek/api-key", profile.CredentialReference);
        Assert.Equal(profile.Id, configurationStore.Persisted.SelectedModelSourceId);
        Assert.Equal(apiKey, secretStore.GetSecretForTest(profile.CredentialReference!));
        Assert.Equal(
            [
                $"secret:resolve:{profile.CredentialReference}",
                $"secret:set:{profile.CredentialReference}",
                "configuration:save"
            ],
            events.ToArray());
        Assert.DoesNotContain(
            apiKey,
            JsonSerializer.Serialize(configurationStore.Persisted),
            StringComparison.Ordinal);
        Assert.Equal(ModelProviderPresets.DeepSeekId, harness.Registry.Sources.Single().PresetId);
        Assert.Equal(
            OpenAiTokenLimitParameter.MaxCompletionTokens,
            harness.Registry.Sources.Single().TokenLimitParameter);
        Assert.Equal(directories.LogDirectoryPath, configurationStore.Persisted.LogDirectoryPath);
        Assert.True(Directory.Exists(directories.LogDirectoryPath));
    }

    [Fact]
    public async Task OnboardingNetworkPresetRemovesNewCredentialWhenConfigurationCommitFails()
    {
        const string apiKey = "onboarding-rollback-secret";
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(new AppConfiguration(), events)
        {
            NextSaveException = new IOException("configuration write failed")
        };
        using var secretStore = new FaultInjectingSecretStore(events);
        using var harness = await CreateHarnessAsync(configurationStore, secretStore);
        using var directories = new TemporaryTestDirectory();
        events.Clear();

        var exception = await Assert.ThrowsAsync<IOException>(() => harness.Service.CompleteAsync(
            new OnboardingSubmission(
                directories.WorkspacePath,
                directories.SandboxPath,
                ConfigureOllama: false,
                new Uri("http://127.0.0.1:11434"),
                "llama3",
                ModelProviderPresets.MiniMaxId,
                new Uri("https://api.minimax.io/v1"),
                "MiniMax-M2.7",
                apiKey.AsMemory(),
                LogDirectoryPath: directories.LogDirectoryPath),
            TestContext.Current.CancellationToken));

        Assert.Equal("configuration write failed", exception.Message);
        const string credentialReference = "model-sources/preset-minimax/api-key";
        Assert.Equal(
            [
                $"secret:resolve:{credentialReference}",
                $"secret:set:{credentialReference}",
                "configuration:save",
                $"secret:delete:{credentialReference}"
            ],
            events.ToArray());
        Assert.Null(secretStore.GetSecretForTest(credentialReference));
        Assert.Empty(configurationStore.Persisted.ModelSources);
        Assert.DoesNotContain(apiKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnboardingRejectsUnknownTokenParameterBeforeCreatingDirectoriesOrSecrets()
    {
        var events = new ConcurrentQueue<string>();
        var configurationStore = new RecordingConfigurationStore(new AppConfiguration(), events);
        using var secretStore = new FaultInjectingSecretStore(events);
        using var harness = await CreateHarnessAsync(configurationStore, secretStore);
        using var directories = new TemporaryTestDirectory();
        events.Clear();

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            harness.Service.CompleteAsync(
                new OnboardingSubmission(
                    directories.WorkspacePath,
                    directories.SandboxPath,
                    ConfigureOllama: false,
                    new Uri("http://127.0.0.1:11434"),
                    "llama3",
                    ModelProviderPresets.KimiId,
                    new Uri("https://api.moonshot.cn/v1"),
                    "kimi-k2.6",
                    "temporary-secret".AsMemory(),
                    (OpenAiTokenLimitParameter)999),
                TestContext.Current.CancellationToken));

        Assert.Equal("parameter", exception.ParamName);
        Assert.False(Directory.Exists(directories.RootPath));
        Assert.Empty(events);
        Assert.Empty(configurationStore.Persisted.ModelSources);
    }

    private static async Task<ServiceHarness> CreateHarnessAsync(
        RecordingConfigurationStore configurationStore,
        ISecretStore secretStore,
        HttpMessageHandler? httpMessageHandler = null)
    {
        var registry = new ModelServiceRegistry();
        var httpClient = new HttpClient(httpMessageHandler ?? new RejectingHttpHandler());
        var service = new SecureAppStateService(
            configurationStore,
            secretStore,
            registry,
            httpClient,
            new StubSandboxRootManager());
        await service.InitializeAsync(TestContext.Current.CancellationToken);
        return new ServiceHarness(service, registry, httpClient);
    }

    private static AppConfiguration CreateConfiguration(ModelSourceProfile profile) => new()
    {
        SelectedModelSourceId = profile.Id,
        ModelSources = [profile]
    };

    private static ModelSourceProfile CloudProfile() => new()
    {
        Id = "cloud",
        DisplayName = "Cloud",
        Provider = ModelProviderKind.OpenAiCompatible,
        Endpoint = "https://models.example.test/v1/",
        ModelName = "model",
        CredentialReference = CredentialReference,
        CredentialFingerprint = "sha256:…deadbeef"
    };

    private static ModelSourceProfile OllamaProfile() => new()
    {
        Id = "ollama",
        DisplayName = "Ollama",
        Provider = ModelProviderKind.Ollama,
        Endpoint = "http://127.0.0.1:11434/",
        ModelName = "llama3"
    };

    private sealed class RecordingConfigurationStore(
        AppConfiguration initial,
        ConcurrentQueue<string> events) : IConfigurationStore<AppConfiguration>
    {
        private TaskCompletionSource? _releaseSave;
        private Exception? _nextSaveException;
        private bool _blockNextSave;

        public AppConfiguration Persisted { get; private set; } = initial;

        public Exception? NextSaveException
        {
            get => _nextSaveException;
            set => _nextSaveException = value;
        }

        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AppConfiguration?> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<AppConfiguration?>(Persisted);
        }

        public async Task SaveAsync(
            AppConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            events.Enqueue("configuration:save");
            if (_blockNextSave)
            {
                _blockNextSave = false;
                SaveEntered.TrySetResult();
                await (_releaseSave ?? throw new InvalidOperationException("Save blocker was not initialized."))
                    .Task.WaitAsync(cancellationToken);
            }

            var exception = Interlocked.Exchange(ref _nextSaveException, null);
            if (exception is not null)
            {
                throw exception;
            }

            Persisted = configuration;
        }

        public void BlockNextSave()
        {
            _releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _blockNextSave = true;
        }

        public void ReleaseSave() =>
            (_releaseSave ?? throw new InvalidOperationException("Save blocker was not initialized."))
                .TrySetResult();
    }

    private sealed class FaultInjectingSecretStore(ConcurrentQueue<string> events) : ISecretStore, IDisposable
    {
        private readonly Dictionary<string, char[]> _secrets = new(StringComparer.Ordinal);
        private readonly Queue<Exception> _deleteFailures = new();
        private readonly Queue<Exception> _setFailures = new();

        public ValueTask<SecretValue?> ResolveAsync(
            string reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Enqueue($"secret:resolve:{reference}");
            lock (_secrets)
            {
                return ValueTask.FromResult<SecretValue?>(
                    _secrets.TryGetValue(reference, out var secret) ? new SecretValue(secret) : null);
            }
        }

        public ValueTask SetAsync(
            string reference,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Enqueue($"secret:set:{reference}");
            lock (_secrets)
            {
                if (_setFailures.TryDequeue(out var failure))
                {
                    throw failure;
                }

                if (_secrets.Remove(reference, out var previous))
                {
                    Clear(previous);
                }

                _secrets.Add(reference, secret.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteAsync(
            string reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Enqueue($"secret:delete:{reference}");
            lock (_secrets)
            {
                if (_deleteFailures.TryDequeue(out var failure))
                {
                    throw failure;
                }

                if (!_secrets.Remove(reference, out var secret))
                {
                    return ValueTask.FromResult(false);
                }

                Clear(secret);
                return ValueTask.FromResult(true);
            }
        }

        public void Seed(string reference, string secret)
        {
            lock (_secrets)
            {
                _secrets.Add(reference, secret.ToCharArray());
            }
        }

        public string? GetSecretForTest(string reference)
        {
            lock (_secrets)
            {
                return _secrets.TryGetValue(reference, out var secret) ? new string(secret) : null;
            }
        }

        public void EnqueueDeleteFailure(Exception exception) => _deleteFailures.Enqueue(exception);

        public void EnqueueSetFailure(Exception exception) => _setFailures.Enqueue(exception);

        public void Dispose()
        {
            lock (_secrets)
            {
                foreach (var secret in _secrets.Values)
                {
                    Clear(secret);
                }

                _secrets.Clear();
            }
        }

        private static void Clear(char[] value) =>
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
    }

    private sealed class StubSandboxRootManager : ICliSandboxRootManager
    {
        private HashSet<string> _roots = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> SandboxRoots => _roots;

        public void ReplaceSandboxRoots(IEnumerable<string> sandboxRoots) =>
            _roots = sandboxRoots.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FailOnceLanguagePreferenceWriter : IAppLanguagePreferenceWriter
    {
        public int Attempts { get; private set; }

        public string? PersistedLanguage { get; private set; }

        public void Save(string language)
        {
            Attempts++;
            if (Attempts == 1)
            {
                throw new IOException("Injected bootstrap preference write failure.");
            }

            PersistedLanguage = language;
        }
    }

    private sealed class RejectingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Network access is not expected in state transaction tests.");
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class TemporaryTestDirectory : IDisposable
    {
        public TemporaryTestDirectory()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "LocaleSmith.App.Tests", Guid.NewGuid().ToString("N"));
            WorkspacePath = Path.Combine(RootPath, "workspace");
            SandboxPath = Path.Combine(RootPath, "sandbox");
            LogDirectoryPath = Path.Combine(RootPath, "logs");
        }

        public string RootPath { get; }

        public string WorkspacePath { get; }

        public string SandboxPath { get; }

        public string LogDirectoryPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class ServiceHarness(
        SecureAppStateService service,
        ModelServiceRegistry registry,
        HttpClient httpClient) : IDisposable
    {
        public SecureAppStateService Service { get; } = service;

        public ModelServiceRegistry Registry { get; } = registry;

        public void Dispose()
        {
            Service.Dispose();
            Registry.Dispose();
            httpClient.Dispose();
        }
    }
}
