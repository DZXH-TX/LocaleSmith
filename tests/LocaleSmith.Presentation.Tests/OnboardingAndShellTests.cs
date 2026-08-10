using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.Presentation.Tests;

public sealed class OnboardingAndShellTests
{
    [Fact]
    public async Task FourStepOnboardingPersistsOnlyAfterSummary()
    {
        var onboarding = new RecordingOnboardingService();
        var viewModel = new OnboardingViewModel(onboarding);
        var completed = false;
        viewModel.Completed += (_, _) => completed = true;

        Assert.Equal(4, OnboardingViewModel.StepCount);
        Assert.Equal(1, viewModel.CurrentStepNumber);

        viewModel.NextCommand.Execute(null);
        Assert.True(viewModel.IsWorkspaceStep);
        viewModel.NextCommand.Execute(null);
        Assert.True(viewModel.IsModelStep);
        viewModel.NextCommand.Execute(null);
        Assert.True(viewModel.IsSummaryStep);
        Assert.Null(onboarding.Submission);

        await viewModel.CompleteCommand.ExecuteAsync(null);

        Assert.NotNull(onboarding.Submission);
        Assert.True(completed);
        Assert.Equal("http://127.0.0.1:11434/", onboarding.Submission.OllamaEndpoint.AbsoluteUri);
        Assert.Equal(
            Path.GetFullPath(viewModel.LogDirectoryPath),
            onboarding.Submission.LogDirectoryPath);
    }

    [Fact]
    public void OnboardingRequiresALogDirectoryBeforeLeavingThePathStep()
    {
        var viewModel = new OnboardingViewModel(new RecordingOnboardingService());
        viewModel.NextCommand.Execute(null);
        viewModel.LogDirectoryPath = string.Empty;

        viewModel.NextCommand.Execute(null);

        Assert.True(viewModel.IsWorkspaceStep);
        Assert.Contains("log", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnboardingBlocksRemotePlaintextOllamaEndpoint()
    {
        var viewModel = new OnboardingViewModel(new RecordingOnboardingService());
        viewModel.NextCommand.Execute(null);
        viewModel.NextCommand.Execute(null);
        viewModel.OllamaEndpoint = "http://models.example.test:11434";

        viewModel.NextCommand.Execute(null);

        Assert.True(viewModel.IsModelStep);
        Assert.Contains("loopback", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnboardingModelPathSelectionIsMutuallyExclusiveAndPreloadsNetworkDefaults()
    {
        var viewModel = new OnboardingViewModel(new RecordingOnboardingService());

        Assert.True(viewModel.IsLocalModelPath);
        Assert.False(viewModel.IsNetworkModelPath);
        Assert.True(viewModel.ConfigureOllama);

        viewModel.SelectModelPath(OnboardingModelPath.NetworkProvider);

        Assert.False(viewModel.IsLocalModelPath);
        Assert.True(viewModel.IsNetworkModelPath);
        Assert.False(viewModel.ConfigureOllama);
        Assert.True(viewModel.ConfigureNetworkProvider);
        Assert.Equal("deepseek", viewModel.SelectedNetworkPreset.Id);
        Assert.Equal("https://api.deepseek.com/", viewModel.NetworkEndpoint);
        Assert.Equal("deepseek-v4-pro", viewModel.NetworkModelName);
        Assert.Equal(OpenAiTokenLimitParameter.MaxTokens, viewModel.NetworkTokenLimitParameter);
        Assert.Equal(9, viewModel.NetworkPresetOptions.Count);

        viewModel.SelectedNetworkPresetId = ModelProviderPresets.XiaomiMimoId;

        Assert.Equal("https://api.xiaomimimo.com/v1", viewModel.NetworkEndpoint);
        Assert.Equal("mimo-v2.5-pro", viewModel.NetworkModelName);
        Assert.Equal(OpenAiTokenLimitParameter.MaxCompletionTokens, viewModel.NetworkTokenLimitParameter);
        Assert.Equal(
            OpenAiTokenLimitParameter.MaxCompletionTokens,
            viewModel.NetworkTokenLimitParameterOption.Value);

        viewModel.NetworkTokenLimitParameterOption = viewModel.NetworkTokenLimitParameterOptions.Single(
            static option => option.Value == OpenAiTokenLimitParameter.Omit);

        Assert.Equal(OpenAiTokenLimitParameter.Omit, viewModel.NetworkTokenLimitParameter);
    }

    [Fact]
    public void NetworkOmitTokenOptionUsesLocalizedLabelAndRemainsTheSelectedObject()
    {
        var text = new DictionaryTextProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ModelSourceTokenLimitParameterOmitOption"] = "由服务端决定（不发送）"
        });
        var viewModel = new OnboardingViewModel(new RecordingOnboardingService(), text);
        var omit = viewModel.NetworkTokenLimitParameterOptions.Single(
            static option => option.Value == OpenAiTokenLimitParameter.Omit);

        viewModel.NetworkTokenLimitParameterOption = omit;

        Assert.Equal("由服务端决定（不发送）", omit.DisplayName);
        Assert.Same(omit, viewModel.NetworkTokenLimitParameterOption);
        Assert.Equal(OpenAiTokenLimitParameter.Omit, viewModel.NetworkTokenLimitParameter);
    }

    [Fact]
    public void NetworkOnboardingCannotAdvanceWithoutAnApiKey()
    {
        var viewModel = new OnboardingViewModel(new RecordingOnboardingService());
        viewModel.SelectModelPath(OnboardingModelPath.NetworkProvider);
        viewModel.NextCommand.Execute(null);
        viewModel.NextCommand.Execute(null);

        viewModel.NextCommand.Execute(null);

        Assert.True(viewModel.IsModelStep);
        Assert.Contains("API key", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetworkOnboardingPassesSecretEphemerallyAndClearsItsBufferAfterSave()
    {
        var onboarding = new RecordingOnboardingService();
        var viewModel = new OnboardingViewModel(onboarding);
        var secretConsumed = false;
        var eventOrder = new List<string>();
        viewModel.SecretInputConsumed += (_, _) =>
        {
            secretConsumed = true;
            eventOrder.Add("secret");
        };
        viewModel.Completed += (_, _) => eventOrder.Add("completed");
        viewModel.SelectModelPath(OnboardingModelPath.NetworkProvider);
        viewModel.NextCommand.Execute(null);
        viewModel.NextCommand.Execute(null);
        viewModel.SetNetworkApiKeyPresent(true);
        viewModel.NetworkTokenLimitParameter = OpenAiTokenLimitParameter.MaxCompletionTokens;
        viewModel.NextCommand.Execute(null);

        await viewModel.CompleteCommand.ExecuteAsync("test-network-secret");

        Assert.NotNull(onboarding.Submission);
        Assert.Equal("deepseek", onboarding.Submission.NetworkPresetId);
        Assert.Equal("https://api.deepseek.com/", onboarding.Submission.NetworkEndpoint?.AbsoluteUri);
        Assert.Equal("deepseek-v4-pro", onboarding.Submission.NetworkModelName);
        Assert.Equal(
            OpenAiTokenLimitParameter.MaxCompletionTokens,
            onboarding.Submission.NetworkTokenLimitParameter);
        Assert.Equal("test-network-secret", onboarding.CapturedNetworkApiKey);
        Assert.True(secretConsumed);
        Assert.All(onboarding.Submission.NetworkApiKey.ToArray(), character => Assert.Equal('\0', character));
        Assert.Equal(["secret", "completed"], eventOrder);
    }

    [Theory]
    [InlineData(true, ShellSection.Dashboard)]
    [InlineData(false, ShellSection.Onboarding)]
    public async Task ShellRoutesFromEncryptedFirstRunState(bool complete, ShellSection expected)
    {
        var viewModel = new ShellViewModel(new MemoryConfigurationService
        {
            Configuration = new AppConfiguration { IsOnboardingComplete = complete }
        });
        ShellSection? navigated = null;
        viewModel.NavigationRequested += (_, section) => navigated = section;

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, viewModel.CurrentSection);
        Assert.Equal(expected, navigated);
        Assert.Equal(complete, viewModel.IsNavigationAvailable);
    }

    private sealed class RecordingOnboardingService : IOnboardingService
    {
        public OnboardingSubmission? Submission { get; private set; }

        public string? CapturedNetworkApiKey { get; private set; }

        public Task CompleteAsync(
            OnboardingSubmission submission,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Submission = submission;
            CapturedNetworkApiKey = submission.NetworkApiKey.IsEmpty
                ? null
                : new string(submission.NetworkApiKey.Span);
            return Task.CompletedTask;
        }
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

    private sealed class MemoryConfigurationService : IAppConfigurationService
    {
        public AppConfiguration Configuration { get; set; } = new();

        public Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Configuration);
        }

        public Task SaveAsync(
            AppConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Configuration = configuration;
            return Task.CompletedTask;
        }

        public Task SaveSettingsAsync(
            AppSettingsUpdate settings,
            CancellationToken cancellationToken = default) =>
            SaveAsync(
                Configuration with
                {
                    Language = settings.Language,
                    Theme = settings.Theme,
                    ForceAppAnimations = settings.ForceAppAnimations,
                    WorkspacePath = settings.WorkspacePath,
                    SandboxPath = settings.SandboxPath,
                    LogDirectoryPath = settings.LogDirectoryPath ?? Configuration.LogDirectoryPath
                },
                cancellationToken);
    }
}
