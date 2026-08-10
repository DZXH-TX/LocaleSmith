using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.Input;
using JaxI18n.Core.Models;
using JaxI18n.Presentation.Abstractions;
using JaxI18n.Presentation.Models;

namespace JaxI18n.Presentation.ViewModels;

public sealed class OnboardingViewModel : ViewModelBase
{
    public const int StepCount = 4;

    private readonly IOnboardingService _onboardingService;
    private readonly IUiTextProvider _text;
    private int _currentStep;
    private string _workspacePath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
        "LocaleSmith");
    private string _sandboxPath = Path.Combine(Path.GetTempPath(), "LocaleSmith", "Sandbox");
    private bool _configureOllama = true;
    private string _ollamaEndpoint = "http://127.0.0.1:11434";
    private string _ollamaModelName = "llama3";
    private OnboardingModelPath _selectedModelPath = OnboardingModelPath.OllamaLocal;
    private bool _configureNetworkProvider;
    private ModelProviderPreset _selectedNetworkPreset = ModelProviderPresets.DeepSeek;
    private string _networkEndpoint = ModelProviderPresets.DeepSeek.DefaultEndpoint!.AbsoluteUri;
    private string _networkModelName = ModelProviderPresets.DeepSeek.DefaultModelName!;
    private OpenAiTokenLimitParameter _networkTokenLimitParameter =
        ModelProviderPresets.DeepSeek.DefaultTokenLimitParameter;
    private bool _hasNetworkApiKey;

    public OnboardingViewModel(
        IOnboardingService onboardingService,
        IUiTextProvider? text = null)
    {
        _onboardingService = onboardingService ?? throw new ArgumentNullException(nameof(onboardingService));
        _text = text ?? FallbackUiTextProvider.Instance;
        BackCommand = new RelayCommand(Back, () => CurrentStep > 0 && !IsBusy);
        NextCommand = new RelayCommand(Next, () => CurrentStep < StepCount - 1 && !IsBusy);
        SkipModelSetupCommand = new RelayCommand(SkipModelSetup, () => CurrentStep == 2 && !IsBusy);
        CompleteCommand = new AsyncRelayCommand<string?>(
            CompleteAsync,
            _ => CurrentStep == StepCount - 1 && !IsBusy);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? Completed;

    public event EventHandler? ExitRequested;

    public event EventHandler? SecretInputConsumed;

    public IRelayCommand BackCommand { get; }

    public IRelayCommand NextCommand { get; }

    public IRelayCommand SkipModelSetupCommand { get; }

    public IAsyncRelayCommand<string?> CompleteCommand { get; }

    public IRelayCommand ExitCommand { get; }

    public int CurrentStep
    {
        get => _currentStep;
        private set
        {
            if (!SetProperty(ref _currentStep, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CurrentStepNumber));
            OnPropertyChanged(nameof(IsWelcomeStep));
            OnPropertyChanged(nameof(IsWorkspaceStep));
            OnPropertyChanged(nameof(IsModelStep));
            OnPropertyChanged(nameof(IsSummaryStep));
            NotifyCommands();
        }
    }

    public int CurrentStepNumber => CurrentStep + 1;

    public bool IsWelcomeStep => CurrentStep == 0;

    public bool IsWorkspaceStep => CurrentStep == 1;

    public bool IsModelStep => CurrentStep == 2;

    public bool IsSummaryStep => CurrentStep == 3;

    public IReadOnlyList<ModelProviderPreset> NetworkPresetOptions { get; } = ModelProviderPresets.All;

    public IReadOnlyList<TokenLimitParameterOption> NetworkTokenLimitParameterOptions { get; } =
    [
        new(OpenAiTokenLimitParameter.MaxTokens, "max_tokens"),
        new(OpenAiTokenLimitParameter.MaxCompletionTokens, "max_completion_tokens")
    ];

    public OnboardingModelPath SelectedModelPath
    {
        get => _selectedModelPath;
        private set
        {
            if (!SetProperty(ref _selectedModelPath, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsLocalModelPath));
            OnPropertyChanged(nameof(IsNetworkModelPath));
            NotifyModelConfigurationProperties();
        }
    }

    public bool IsLocalModelPath => SelectedModelPath == OnboardingModelPath.OllamaLocal;

    public bool IsNetworkModelPath => SelectedModelPath == OnboardingModelPath.NetworkProvider;

    public string WorkspacePath
    {
        get => _workspacePath;
        set => SetProperty(ref _workspacePath, value);
    }

    public string SandboxPath
    {
        get => _sandboxPath;
        set => SetProperty(ref _sandboxPath, value);
    }

    public bool ConfigureOllama
    {
        get => _configureOllama;
        set
        {
            if (SetProperty(ref _configureOllama, value))
            {
                NotifyModelConfigurationProperties();
            }
        }
    }

    public string OllamaEndpoint
    {
        get => _ollamaEndpoint;
        set => SetProperty(ref _ollamaEndpoint, value);
    }

    public string OllamaModelName
    {
        get => _ollamaModelName;
        set => SetProperty(ref _ollamaModelName, value);
    }

    public bool ConfigureNetworkProvider
    {
        get => _configureNetworkProvider;
        private set
        {
            if (SetProperty(ref _configureNetworkProvider, value))
            {
                NotifyModelConfigurationProperties();
            }
        }
    }

    public bool IsLocalConfigurationEnabled => IsLocalModelPath && ConfigureOllama;

    public bool IsNetworkConfigurationEnabled => IsNetworkModelPath && ConfigureNetworkProvider;

    public bool IsModelConfigurationSkipped =>
        !IsLocalConfigurationEnabled && !IsNetworkConfigurationEnabled;

    public ModelProviderPreset SelectedNetworkPreset
    {
        get => _selectedNetworkPreset;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetProperty(ref _selectedNetworkPreset, value))
            {
                return;
            }

            NetworkEndpoint = value.DefaultEndpoint?.AbsoluteUri ?? string.Empty;
            NetworkModelName = value.DefaultModelName ?? string.Empty;
            NetworkTokenLimitParameter = value.DefaultTokenLimitParameter;
        }
    }

    public string NetworkEndpoint
    {
        get => _networkEndpoint;
        set => SetProperty(ref _networkEndpoint, value);
    }

    public string NetworkModelName
    {
        get => _networkModelName;
        set => SetProperty(ref _networkModelName, value);
    }

    public OpenAiTokenLimitParameter NetworkTokenLimitParameter
    {
        get => _networkTokenLimitParameter;
        set => SetProperty(ref _networkTokenLimitParameter, value);
    }

    public void SelectModelPath(OnboardingModelPath path)
    {
        if (path == OnboardingModelPath.OllamaLocal && _hasNetworkApiKey)
        {
            _hasNetworkApiKey = false;
            SecretInputConsumed?.Invoke(this, EventArgs.Empty);
        }

        SelectedModelPath = path;
        ConfigureOllama = path == OnboardingModelPath.OllamaLocal;
        ConfigureNetworkProvider = path == OnboardingModelPath.NetworkProvider;
        ErrorMessage = null;
    }

    public void SetNetworkApiKeyPresent(bool isPresent) => _hasNetworkApiKey = isPresent;

    private void Back()
    {
        ErrorMessage = null;
        CurrentStep = Math.Max(0, CurrentStep - 1);
    }

    private void Next()
    {
        ErrorMessage = null;
        if (!ValidateCurrentStep())
        {
            return;
        }

        CurrentStep = Math.Min(StepCount - 1, CurrentStep + 1);
    }

    private void SkipModelSetup()
    {
        ConfigureOllama = false;
        ConfigureNetworkProvider = false;
        _hasNetworkApiKey = false;
        SecretInputConsumed?.Invoke(this, EventArgs.Empty);
        ErrorMessage = null;
        CurrentStep = StepCount - 1;
    }

    private async Task CompleteAsync(string? networkApiKey)
    {
        if (!ValidateAll())
        {
            return;
        }

        char[]? networkSecret = null;
        var completed = false;
        IsBusy = true;
        NotifyCommands();
        ErrorMessage = null;
        StatusMessage = Text("OnboardingEncrypting", "Encrypting settings and completing setup…");
        try
        {
            if (IsNetworkModelPath && ConfigureNetworkProvider)
            {
                if (string.IsNullOrWhiteSpace(networkApiKey))
                {
                    ErrorMessage = Text("OnboardingNetworkApiKeyRequired", "Enter the API key for the selected provider.");
                    StatusMessage = null;
                    return;
                }

                networkSecret = networkApiKey.ToCharArray();
            }

            var submission = new OnboardingSubmission(
                Path.GetFullPath(WorkspacePath),
                Path.GetFullPath(SandboxPath),
                IsLocalModelPath && ConfigureOllama,
                new Uri(OllamaEndpoint, UriKind.Absolute),
                OllamaModelName.Trim(),
                IsNetworkModelPath && ConfigureNetworkProvider ? SelectedNetworkPreset.Id : null,
                IsNetworkModelPath && ConfigureNetworkProvider
                    ? new Uri(NetworkEndpoint, UriKind.Absolute)
                    : null,
                IsNetworkModelPath && ConfigureNetworkProvider ? NetworkModelName.Trim() : null,
                networkSecret is null ? ReadOnlyMemory<char>.Empty : networkSecret.AsMemory(),
                IsNetworkModelPath && ConfigureNetworkProvider ? NetworkTokenLimitParameter : null);
            await _onboardingService.CompleteAsync(submission).ConfigureAwait(true);
            StatusMessage = Text("OnboardingComplete", "Setup complete.");
            completed = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = Text(
                "OnboardingSaveFailed",
                "Settings could not be saved securely: {0}",
                exception.Message);
            StatusMessage = null;
        }
        finally
        {
            if (networkSecret is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(networkSecret.AsSpan()));
                SecretInputConsumed?.Invoke(this, EventArgs.Empty);
            }

            IsBusy = false;
            NotifyCommands();
        }

        if (completed)
        {
            Completed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool ValidateCurrentStep() => CurrentStep switch
    {
        0 => true,
        1 => ValidatePaths(),
        2 => ValidateModel(),
        _ => true
    };

    private bool ValidateAll() => ValidatePaths() && ValidateModel();

    private bool ValidatePaths()
    {
        if (string.IsNullOrWhiteSpace(WorkspacePath) || string.IsNullOrWhiteSpace(SandboxPath))
        {
            ErrorMessage = Text("OnboardingPathsRequired", "Workspace and sandbox paths are required.");
            return false;
        }

        try
        {
            _ = Path.GetFullPath(WorkspacePath);
            _ = Path.GetFullPath(SandboxPath);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ErrorMessage = Text("OnboardingPathsInvalid", "Workspace or sandbox path is invalid.");
            return false;
        }
    }

    private bool ValidateModel()
    {
        if (IsNetworkModelPath)
        {
            return ValidateNetworkModel();
        }

        if (!ConfigureOllama)
        {
            return true;
        }

        if (!Uri.TryCreate(OllamaEndpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            ErrorMessage = Text("OnboardingOllamaAddressInvalid", "Enter an absolute HTTP or HTTPS Ollama address.");
            return false;
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
        {
            ErrorMessage = Text(
                "OnboardingOllamaHttpBlocked",
                "Unencrypted HTTP is allowed only for a loopback Ollama service.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(OllamaModelName))
        {
            ErrorMessage = Text("OnboardingModelRequired", "Enter an installed Ollama model name.");
            return false;
        }

        return true;
    }

    private bool ValidateNetworkModel()
    {
        if (!ConfigureNetworkProvider)
        {
            return true;
        }

        if (!Uri.TryCreate(NetworkEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            ErrorMessage = Text(
                "OnboardingNetworkAddressInvalid",
                "Enter an absolute HTTPS address for the selected network provider.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(NetworkModelName))
        {
            ErrorMessage = Text("OnboardingNetworkModelRequired", "Enter a network model name.");
            return false;
        }

        if (!_hasNetworkApiKey)
        {
            ErrorMessage = Text(
                "OnboardingNetworkApiKeyRequired",
                "Enter the API key for the selected provider.");
            return false;
        }

        return true;
    }

    private void NotifyCommands()
    {
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        SkipModelSetupCommand.NotifyCanExecuteChanged();
        CompleteCommand.NotifyCanExecuteChanged();
    }

    private void NotifyModelConfigurationProperties()
    {
        OnPropertyChanged(nameof(IsLocalConfigurationEnabled));
        OnPropertyChanged(nameof(IsNetworkConfigurationEnabled));
        OnPropertyChanged(nameof(IsModelConfigurationSkipped));
    }

    private string Text(string key, string fallback, params object?[] arguments) =>
        _text.GetText(key, fallback, arguments);
}
