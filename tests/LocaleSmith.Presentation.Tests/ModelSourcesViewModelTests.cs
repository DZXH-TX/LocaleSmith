using System.Text.Json;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.Presentation.Tests;

public sealed class ModelSourcesViewModelTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SavePassesSecretOnlyToCatalogAndClearsInputLifecycle()
    {
        var catalog = new MemoryModelSourceCatalog();
        var viewModel = new ModelSourcesViewModel(catalog);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Provider = ModelProviderKind.OpenAiCompatible;
        viewModel.ConfirmProviderChangeCommand.Execute(null);
        viewModel.Endpoint = "https://models.example.test/v1";
        viewModel.ModelName = "example-model";
        viewModel.MaxOutputTokens = 16_384;
        viewModel.MaxSourceCharactersPerRequest = 32_000;
        var consumed = false;
        viewModel.SecretInputConsumed += (_, _) => consumed = true;

        await viewModel.SaveAsync("top-secret", TestContext.Current.CancellationToken);

        Assert.Equal("top-secret", catalog.LastApiKey);
        Assert.Equal(16_384, catalog.LastSavedSource?.MaxOutputTokens);
        Assert.Equal(32_000, catalog.LastSavedSource?.MaxSourceCharactersPerRequest);
        Assert.True(consumed);
        Assert.DoesNotContain("top-secret", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", viewModel.CredentialReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteHttpEndpointIsBlockedBeforeConnectionAttempt()
    {
        var catalog = new MemoryModelSourceCatalog();
        var viewModel = new ModelSourcesViewModel(catalog);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Provider = ModelProviderKind.OpenAiCompatible;
        viewModel.ConfirmProviderChangeCommand.Execute(null);
        viewModel.Endpoint = "http://models.example.test/v1";

        await viewModel.TestConnectionAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(0, catalog.TestConnectionCount);
        Assert.Contains("HTTPS", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepSeekCompatibleConnectionTestPreservesCustomEndpointModelAndKey()
    {
        var catalog = new MemoryModelSourceCatalog();
        var viewModel = new ModelSourcesViewModel(catalog);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Provider = ModelProviderKind.OpenAiCompatible;
        viewModel.ConfirmProviderChangeCommand.Execute(null);
        viewModel.DisplayName = "DeepSeek";
        viewModel.Endpoint = "https://api.deepseek.com/v1/chat/completions";
        viewModel.ModelName = "deepseek-chat";

        await viewModel.TestConnectionAsync("deepseek-secret", TestContext.Current.CancellationToken);

        Assert.Equal(1, catalog.TestConnectionCount);
        Assert.Equal(ModelProviderKind.OpenAiCompatible, catalog.LastTestSource?.Provider);
        Assert.Equal("https://api.deepseek.com/v1/chat/completions", catalog.LastTestSource?.Endpoint.AbsoluteUri);
        Assert.Equal("deepseek-chat", catalog.LastTestSource?.ModelName);
        Assert.Equal("deepseek-secret", catalog.LastTestApiKey);
        Assert.Equal(ConnectionTestState.Successful, viewModel.ConnectionState);
    }

    [Fact]
    public async Task EditingNamedPresetToCustomGatewayDemotesPresetWithoutChangingConnectionFields()
    {
        var catalog = new MemoryModelSourceCatalog();
        var viewModel = new ModelSourcesViewModel(catalog);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Provider = ModelProviderKind.OpenAiCompatible;

        viewModel.SelectedPreset = ModelProviderPresets.DeepSeek;

        Assert.Equal("DeepSeek", viewModel.DisplayName);
        Assert.Equal("https://api.deepseek.com/", viewModel.Endpoint);
        Assert.Equal("deepseek-v4-pro", viewModel.ModelName);
        Assert.Equal(OpenAiTokenLimitParameter.MaxTokens, viewModel.SelectedTokenLimitParameter);
        viewModel.SelectedTokenLimitParameter = OpenAiTokenLimitParameter.MaxCompletionTokens;
        viewModel.Endpoint = "https://gateway.example.test/deepseek/v1";
        viewModel.ModelName = "account-specific-model";

        Assert.Equal(ModelProviderPresets.CustomId, viewModel.SelectedPresetId);
        Assert.Equal("https://gateway.example.test/deepseek/v1", viewModel.Endpoint);
        Assert.Equal("account-specific-model", viewModel.ModelName);
        Assert.Equal(OpenAiTokenLimitParameter.MaxCompletionTokens, viewModel.SelectedTokenLimitParameter);

        await viewModel.SaveAsync("deepseek-secret", TestContext.Current.CancellationToken);

        Assert.Equal(ModelProviderPresets.CustomId, catalog.LastSavedSource?.PresetId);
        Assert.Equal("https://gateway.example.test/deepseek/v1", catalog.LastSavedSource?.Endpoint.AbsoluteUri);
        Assert.Equal("account-specific-model", catalog.LastSavedSource?.ModelName);
        Assert.Equal(OpenAiTokenLimitParameter.MaxCompletionTokens, catalog.LastSavedSource?.TokenLimitParameter);
        Assert.Equal(ModelProviderPresets.CustomId, viewModel.SelectedPreset.Id);
    }

    [Theory]
    [InlineData(
        ModelProviderPresets.DeepSeekId,
        "https://api.deepseek.com/",
        "deepseek-v4-pro",
        OpenAiTokenLimitParameter.MaxTokens)]
    [InlineData(
        ModelProviderPresets.ZhipuGlmId,
        "https://open.bigmodel.cn/api/paas/v4",
        "glm-5.2",
        OpenAiTokenLimitParameter.MaxTokens)]
    [InlineData(
        ModelProviderPresets.XiaomiMimoId,
        "https://api.xiaomimimo.com/v1",
        "mimo-v2.5-pro",
        OpenAiTokenLimitParameter.MaxCompletionTokens)]
    [InlineData(
        ModelProviderPresets.MiniMaxId,
        "https://api.minimax.io/v1",
        "MiniMax-M2.7",
        OpenAiTokenLimitParameter.MaxCompletionTokens)]
    public async Task PresetIdSelectionReplacesOllamaDefaultsAndKeepsTokenSelectionVisible(
        string presetId,
        string expectedEndpoint,
        string expectedModel,
        OpenAiTokenLimitParameter expectedTokenParameter)
    {
        var viewModel = new ModelSourcesViewModel(new MemoryModelSourceCatalog());
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Provider = ModelProviderKind.OpenAiCompatible;

        // SelectedPresetId is the stable value used by the WinUI ComboBox binding.
        viewModel.SelectedPresetId = presetId;

        Assert.Equal(presetId, viewModel.SelectedPresetId);
        Assert.Equal(expectedEndpoint, viewModel.Endpoint);
        Assert.Equal(expectedModel, viewModel.ModelName);
        Assert.Equal(expectedTokenParameter, viewModel.SelectedTokenLimitParameter);
        Assert.Equal(expectedTokenParameter, viewModel.SelectedTokenLimitParameterOption.Value);
    }

    [Fact]
    public async Task TokenLimitFieldCanBeExplicitlyOmittedForConnectionTestsAndSavedRequests()
    {
        var catalog = new MemoryModelSourceCatalog();
        var viewModel = new ModelSourcesViewModel(catalog);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Provider = ModelProviderKind.OpenAiCompatible;
        viewModel.SelectedPresetId = ModelProviderPresets.DeepSeekId;
        viewModel.SelectedTokenLimitParameterOption = viewModel.TokenLimitParameterOptions.Single(
            static option => option.Value == OpenAiTokenLimitParameter.Omit);
        Assert.False(viewModel.SendsOutputTokenBudget);

        await viewModel.TestConnectionAsync("secret", TestContext.Current.CancellationToken);

        Assert.Equal(OpenAiTokenLimitParameter.Omit, catalog.LastTestSource?.TokenLimitParameter);
        Assert.Equal(OpenAiTokenLimitParameter.Omit, viewModel.SelectedTokenLimitParameter);
        Assert.Contains(
            "do not send",
            viewModel.SelectedTokenLimitParameterOption.DisplayName,
            StringComparison.OrdinalIgnoreCase);

        await viewModel.SaveAsync("secret", TestContext.Current.CancellationToken);

        Assert.Equal(OpenAiTokenLimitParameter.Omit, catalog.LastSavedSource?.TokenLimitParameter);
        Assert.Equal(OpenAiTokenLimitParameter.Omit, viewModel.SelectedTokenLimitParameter);
    }

    [Fact]
    public void OmitTokenOptionUsesLocalizedLabelAndRemainsTheSelectedObject()
    {
        var text = new DictionaryTextProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ModelSourceTokenLimitParameterOmitOption"] = "由服务端决定（不发送）"
        });
        var viewModel = new ModelSourcesViewModel(new MemoryModelSourceCatalog(), text);
        var omit = viewModel.TokenLimitParameterOptions.Single(
            static option => option.Value == OpenAiTokenLimitParameter.Omit);

        viewModel.SelectedTokenLimitParameterOption = omit;

        Assert.Equal("由服务端决定（不发送）", omit.DisplayName);
        Assert.Same(omit, viewModel.SelectedTokenLimitParameterOption);
        Assert.Equal(OpenAiTokenLimitParameter.Omit, viewModel.SelectedTokenLimitParameter);
    }

    [Fact]
    public async Task LoadingMismatchedPresetProfileDemotesPresetAndPreservesConnectionFields()
    {
        var catalog = new MemoryModelSourceCatalog();
        catalog.Seed(new ModelSourceProfile
        {
            Id = "legacy-deepseek",
            DisplayName = "DeepSeek",
            Provider = ModelProviderKind.OpenAiCompatible,
            PresetId = ModelProviderPresets.DeepSeekId,
            Endpoint = "http://127.0.0.1:11434",
            ModelName = "llama3"
        });
        var viewModel = new ModelSourcesViewModel(catalog);

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ModelProviderPresets.CustomId, viewModel.SelectedPresetId);
        Assert.Equal("http://127.0.0.1:11434", viewModel.Endpoint);
        Assert.Equal("llama3", viewModel.ModelName);
        Assert.Equal(OpenAiTokenLimitParameter.MaxTokens, viewModel.SelectedTokenLimitParameter);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task EditingOfficialPresetWithinItsAllowedHostKeepsTheNamedPreset()
    {
        var viewModel = new ModelSourcesViewModel(new MemoryModelSourceCatalog());
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Provider = ModelProviderKind.OpenAiCompatible;
        viewModel.SelectedPresetId = ModelProviderPresets.DeepSeekId;

        viewModel.Endpoint = "https://api.deepseek.com/v1/chat/completions";
        viewModel.ModelName = "a-new-model-name";

        Assert.Equal(ModelProviderPresets.DeepSeekId, viewModel.SelectedPresetId);
        Assert.Equal("a-new-model-name", viewModel.ModelName);
    }

    [Fact]
    public async Task CustomOpenAiCompatibleLoopbackAcceptsExplicitV1Base()
    {
        var catalog = new MemoryModelSourceCatalog();
        var viewModel = new ModelSourcesViewModel(catalog);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Provider = ModelProviderKind.OpenAiCompatible;
        viewModel.Endpoint = "http://127.0.0.1:11434/v1";

        await viewModel.TestConnectionAsync("local-key", TestContext.Current.CancellationToken);

        Assert.Equal(1, catalog.TestConnectionCount);
        Assert.Equal(ModelProviderPresets.CustomId, catalog.LastTestSource?.PresetId);
        Assert.Equal("http://127.0.0.1:11434/v1", catalog.LastTestSource?.Endpoint.AbsoluteUri.TrimEnd('/'));
    }

    [Fact]
    public async Task CustomOpenAiCompatibleLoopbackRejectsProviderNativeRootWithoutV1()
    {
        var catalog = new MemoryModelSourceCatalog();
        var viewModel = new ModelSourcesViewModel(catalog);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Provider = ModelProviderKind.OpenAiCompatible;

        await viewModel.TestConnectionAsync("local-key", TestContext.Current.CancellationToken);

        Assert.Equal(0, catalog.TestConnectionCount);
        Assert.Contains("/v1", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyProfileWithoutPresetIdDeserializesAsCustomWithoutChangingConnectionFields()
    {
        const string json =
            "{\"id\":\"legacy\",\"displayName\":\"Legacy\",\"provider\":1," +
            "\"endpoint\":\"https://legacy.example/v1\",\"modelName\":\"legacy-model\"," +
            "\"credentialReference\":\"model-sources/legacy/api-key\"}";

        var profile = JsonSerializer.Deserialize<ModelSourceProfile>(
            json,
            WebJsonOptions);

        Assert.NotNull(profile);
        Assert.Equal(ModelProviderPresets.CustomId, profile.PresetId);
        Assert.Equal("https://legacy.example/v1", profile.Endpoint);
        Assert.Equal("legacy-model", profile.ModelName);
        Assert.Equal("model-sources/legacy/api-key", profile.CredentialReference);
        Assert.Null(profile.TokenLimitParameter);

        var runtimeSource = new ModelSource(
            profile.Id,
            profile.DisplayName,
            profile.Provider,
            new Uri(profile.Endpoint),
            profile.ModelName,
            profile.CredentialReference,
            profile.PresetId,
            profile.TokenLimitParameter);
        Assert.Equal(OpenAiTokenLimitParameter.MaxTokens, runtimeSource.TokenLimitParameter);
    }

    [Fact]
    public void TokenLimitParameterPersistsWithStableApiFieldName()
    {
        var profile = new ModelSourceProfile
        {
            Id = "custom",
            DisplayName = "Custom",
            Provider = ModelProviderKind.OpenAiCompatible,
            PresetId = ModelProviderPresets.CustomId,
            TokenLimitParameter = OpenAiTokenLimitParameter.MaxCompletionTokens,
            Endpoint = "https://models.example.test/v1",
            ModelName = "model",
            MaxOutputTokens = 16_384,
            MaxSourceCharactersPerRequest = 32_000
        };

        var json = JsonSerializer.Serialize(profile, WebJsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ModelSourceProfile>(json, WebJsonOptions);

        Assert.Contains("\"tokenLimitParameter\":\"max_completion_tokens\"", json, StringComparison.Ordinal);
        Assert.Equal(OpenAiTokenLimitParameter.MaxCompletionTokens, roundTrip?.TokenLimitParameter);
        Assert.Equal(16_384, roundTrip?.MaxOutputTokens);
        Assert.Equal(32_000, roundTrip?.MaxSourceCharactersPerRequest);
    }

    [Fact]
    public void OmittedTokenLimitParameterPersistsWithStableApiValue()
    {
        var profile = new ModelSourceProfile
        {
            Id = "custom",
            DisplayName = "Custom",
            Provider = ModelProviderKind.OpenAiCompatible,
            PresetId = ModelProviderPresets.CustomId,
            TokenLimitParameter = OpenAiTokenLimitParameter.Omit,
            Endpoint = "https://models.example.test/v1",
            ModelName = "model"
        };

        var json = JsonSerializer.Serialize(profile, WebJsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ModelSourceProfile>(json, WebJsonOptions);

        Assert.Contains("\"tokenLimitParameter\":\"omit\"", json, StringComparison.Ordinal);
        Assert.Equal(OpenAiTokenLimitParameter.Omit, roundTrip?.TokenLimitParameter);
    }

    [Fact]
    public async Task KimiPresetUsesRecommendedTokenParameterWhileKeepingItEditable()
    {
        var viewModel = new ModelSourcesViewModel(new MemoryModelSourceCatalog());
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Provider = ModelProviderKind.OpenAiCompatible;

        viewModel.SelectedPreset = ModelProviderPresets.Kimi;

        Assert.Equal("https://api.moonshot.cn/v1", viewModel.Endpoint.TrimEnd('/'));
        Assert.Equal("kimi-k3", viewModel.ModelName);
        Assert.Equal(OpenAiTokenLimitParameter.MaxCompletionTokens, viewModel.SelectedTokenLimitParameter);
        viewModel.SelectedTokenLimitParameter = OpenAiTokenLimitParameter.MaxTokens;
        Assert.Equal(OpenAiTokenLimitParameter.MaxTokens, viewModel.SelectedTokenLimitParameter);
    }

    [Fact]
    public async Task EditingDuringConnectionTestDiscardsStaleResultAndDisablesMutatingCommands()
    {
        var catalog = new MemoryModelSourceCatalog
        {
            PendingTestResult = new TaskCompletionSource<ModelConnectionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = new ModelSourcesViewModel(catalog);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Provider = ModelProviderKind.OpenAiCompatible;
        viewModel.Endpoint = "https://first.example.test/v1";
        viewModel.ModelName = "first-model";

        var testTask = viewModel.TestConnectionAsync("secret", TestContext.Current.CancellationToken);
        await catalog.TestConnectionEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.NewCommand.CanExecute(null));
        viewModel.Endpoint = "https://second.example.test/v1";
        catalog.PendingTestResult.SetResult(ModelConnectionResult.Success("old endpoint succeeded"));
        await testTask;

        Assert.False(viewModel.IsBusy);
        Assert.Equal(ConnectionTestState.NotTested, viewModel.ConnectionState);
        Assert.Null(viewModel.ConnectionMessage);
    }

    [Fact]
    public async Task ProviderChangeNeverSilentlyDiscardsDirtyForm()
    {
        var viewModel = new ModelSourcesViewModel(new MemoryModelSourceCatalog());
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.DisplayName = "Unsaved name";

        viewModel.Provider = ModelProviderKind.Anthropic;

        Assert.Equal(ModelProviderKind.Ollama, viewModel.Provider);
        Assert.Equal(ModelProviderKind.Anthropic, viewModel.PendingProvider);
        Assert.Equal("Unsaved name", viewModel.DisplayName);
        viewModel.CancelProviderChangeCommand.Execute(null);
        Assert.Equal(ModelProviderKind.Ollama, viewModel.Provider);
    }

    [Fact]
    public async Task FailedOllamaRefreshPreservesManualModelName()
    {
        var catalog = new MemoryModelSourceCatalog { FailCatalogRefresh = true };
        var viewModel = new ModelSourcesViewModel(catalog);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.ModelName = "manual-model";

        await viewModel.RefreshModelsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("manual-model", viewModel.ModelName);
        Assert.Equal(ModelCatalogState.Failed, viewModel.CatalogState);
        Assert.Equal(ConnectionTestState.NotTested, viewModel.ConnectionState);
        Assert.Contains("manual", viewModel.CatalogMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeepSeekCatalogRefreshUsesCurrentKeyAndPreservesManualModelUntilSelection()
    {
        var catalog = new MemoryModelSourceCatalog
        {
            CatalogModels =
            [
                new AvailableModelInfo("deepseek-chat", null, null, null, null, null, null),
                new AvailableModelInfo("deepseek-reasoner", null, null, null, null, null, null)
            ]
        };
        var viewModel = new ModelSourcesViewModel(catalog);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Provider = ModelProviderKind.OpenAiCompatible;
        viewModel.ConfirmProviderChangeCommand.Execute(null);
        viewModel.SelectedPreset = ModelProviderPresets.DeepSeek;
        viewModel.ModelName = "manual-account-model";
        var secretConsumed = false;
        viewModel.SecretInputConsumed += (_, _) => secretConsumed = true;

        await viewModel.RefreshModelsAsync(
            "deepseek-secret",
            TestContext.Current.CancellationToken);

        Assert.True(viewModel.CanDiscoverModels);
        Assert.Equal(ModelCatalogState.Fresh, viewModel.CatalogState);
        Assert.Equal(ConnectionTestState.NotTested, viewModel.ConnectionState);
        Assert.Equal("deepseek-secret", catalog.LastCatalogApiKey);
        Assert.Equal(ModelProviderKind.OpenAiCompatible, catalog.LastCatalogSource?.Provider);
        Assert.Equal("https://api.deepseek.com/", catalog.LastCatalogSource?.Endpoint.AbsoluteUri);
        Assert.Equal("manual-account-model", viewModel.ModelName);
        Assert.Null(viewModel.SelectedAvailableModel);
        Assert.True(secretConsumed);

        viewModel.SelectedAvailableModel = viewModel.AvailableModels.Single(model =>
            model.Name == "deepseek-reasoner");

        Assert.Equal("deepseek-reasoner", viewModel.ModelName);
    }

    [Fact]
    public async Task NewSourceRestoresOllamaVisibilityStateAfterEditingCloudSource()
    {
        var viewModel = new ModelSourcesViewModel(new MemoryModelSourceCatalog());
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Provider = ModelProviderKind.OpenAiCompatible;
        viewModel.ConfirmProviderChangeCommand.Execute(null);
        Assert.True(viewModel.IsApiKeyRequired);
        Assert.False(viewModel.IsOllama);

        viewModel.NewCommand.Execute(null);

        Assert.False(viewModel.IsApiKeyRequired);
        Assert.True(viewModel.IsOllama);
    }

    private sealed class DictionaryTextProvider(IReadOnlyDictionary<string, string> values) : IUiTextProvider
    {
        public string GetText(string key, string fallback, params object?[] arguments)
        {
            var template = values.GetValueOrDefault(key, fallback);
            return arguments.Length == 0
                ? template
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, template, arguments);
        }
    }

    private sealed class MemoryModelSourceCatalog : IModelSourceCatalog
    {
        private readonly List<ModelSourceProfile> _profiles = [];

        public string? LastApiKey { get; private set; }

        public ModelSourceDraft? LastSavedSource { get; private set; }

        public int TestConnectionCount { get; private set; }

        public ModelSourceDraft? LastTestSource { get; private set; }

        public string? LastTestApiKey { get; private set; }

        public bool FailCatalogRefresh { get; init; }

        public IReadOnlyList<AvailableModelInfo> CatalogModels { get; init; } =
            [new AvailableModelInfo("llama3", null, null, null, null, null, null)];

        public ModelSourceDraft? LastCatalogSource { get; private set; }

        public string? LastCatalogApiKey { get; private set; }

        public TaskCompletionSource TestConnectionEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ModelConnectionResult>? PendingTestResult { get; init; }

        public void Seed(ModelSourceProfile profile) => _profiles.Add(profile);

        public Task<IReadOnlyList<ModelSourceProfile>> GetAllAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ModelSourceProfile>>(_profiles.ToArray());
        }

        public Task<ModelSourceProfile> SaveAsync(
            ModelSourceDraft source,
            ReadOnlyMemory<char> apiKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastApiKey = apiKey.IsEmpty ? null : new string(apiKey.Span);
            LastSavedSource = source;
            var profile = new ModelSourceProfile
            {
                Id = source.Id ?? "generated",
                DisplayName = source.DisplayName,
                Provider = source.Provider,
                PresetId = source.PresetId,
                TokenLimitParameter = source.TokenLimitParameter,
                Endpoint = source.Endpoint.AbsoluteUri,
                ModelName = source.ModelName,
                MaxOutputTokens = source.MaxOutputTokens,
                MaxSourceCharactersPerRequest = source.MaxSourceCharactersPerRequest,
                CredentialReference = source.Provider == ModelProviderKind.Ollama ? null : "model/generated/key",
                CredentialFingerprint = source.Provider == ModelProviderKind.Ollama ? null : "sha256:…12345678"
            };
            _profiles.RemoveAll(existing => existing.Id == profile.Id);
            _profiles.Add(profile);
            return Task.FromResult(profile);
        }

        public Task<bool> DeleteAsync(string sourceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_profiles.RemoveAll(source => source.Id == sourceId) > 0);
        }

        public async Task<ModelConnectionResult> TestConnectionAsync(
            ModelSourceDraft source,
            ReadOnlyMemory<char> apiKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestConnectionCount++;
            LastTestSource = source;
            LastTestApiKey = apiKey.IsEmpty ? null : new string(apiKey.Span);
            TestConnectionEntered.TrySetResult();
            return PendingTestResult is null
                ? ModelConnectionResult.Success("Connected")
                : await PendingTestResult.Task.WaitAsync(cancellationToken);
        }

        public Task<IReadOnlyList<AvailableModelInfo>> ListAvailableModelsAsync(
            ModelSourceDraft source,
            CancellationToken cancellationToken = default) =>
            ListAvailableModelsAsync(source, ReadOnlyMemory<char>.Empty, cancellationToken);

        public Task<IReadOnlyList<AvailableModelInfo>> ListAvailableModelsAsync(
            ModelSourceDraft source,
            ReadOnlyMemory<char> apiKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCatalogSource = source;
            LastCatalogApiKey = apiKey.IsEmpty ? null : new string(apiKey.Span);
            if (FailCatalogRefresh)
            {
                throw new HttpRequestException("Ollama unavailable");
            }

            return Task.FromResult(CatalogModels);
        }
    }
}
