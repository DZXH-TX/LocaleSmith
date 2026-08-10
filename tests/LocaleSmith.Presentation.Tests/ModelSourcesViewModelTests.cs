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
        var consumed = false;
        viewModel.SecretInputConsumed += (_, _) => consumed = true;

        await viewModel.SaveAsync("top-secret", TestContext.Current.CancellationToken);

        Assert.Equal("top-secret", catalog.LastApiKey);
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
    public async Task SelectingPresetPrefillsButDoesNotLockEndpointOrModel()
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
        viewModel.Endpoint = "https://gateway.example.test/deepseek/v1";
        viewModel.ModelName = "account-specific-model";
        viewModel.SelectedTokenLimitParameter = OpenAiTokenLimitParameter.MaxCompletionTokens;

        await viewModel.SaveAsync("deepseek-secret", TestContext.Current.CancellationToken);

        Assert.Equal(ModelProviderPresets.DeepSeekId, catalog.LastSavedSource?.PresetId);
        Assert.Equal("https://gateway.example.test/deepseek/v1", catalog.LastSavedSource?.Endpoint.AbsoluteUri);
        Assert.Equal("account-specific-model", catalog.LastSavedSource?.ModelName);
        Assert.Equal(OpenAiTokenLimitParameter.MaxCompletionTokens, catalog.LastSavedSource?.TokenLimitParameter);
        Assert.Equal(ModelProviderPresets.DeepSeekId, viewModel.SelectedPreset.Id);
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
            ModelName = "model"
        };

        var json = JsonSerializer.Serialize(profile, WebJsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ModelSourceProfile>(json, WebJsonOptions);

        Assert.Contains("\"tokenLimitParameter\":\"max_completion_tokens\"", json, StringComparison.Ordinal);
        Assert.Equal(OpenAiTokenLimitParameter.MaxCompletionTokens, roundTrip?.TokenLimitParameter);
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
        Assert.True(viewModel.IsConnectionFailure);
        Assert.Contains("manual", viewModel.ConnectionMessage, StringComparison.OrdinalIgnoreCase);
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

        public TaskCompletionSource TestConnectionEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ModelConnectionResult>? PendingTestResult { get; init; }

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
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailCatalogRefresh)
            {
                throw new HttpRequestException("Ollama unavailable");
            }

            return Task.FromResult<IReadOnlyList<AvailableModelInfo>>(
                [new AvailableModelInfo("llama3", null, null, null, null, null, null)]);
        }
    }
}
