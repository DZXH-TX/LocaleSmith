using System.Collections.ObjectModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.Presentation.ViewModels;

public enum ConnectionTestState
{
    NotTested,
    Testing,
    Successful,
    Failed
}

public enum ModelCatalogState
{
    NotLoaded,
    Loading,
    Fresh,
    Empty,
    Unsupported,
    Failed
}

public sealed record TokenLimitParameterOption(
    OpenAiTokenLimitParameter Value,
    string DisplayName);

public sealed class ModelSourceListItemViewModel(ModelSourceProfile profile) : ObservableObject
{
    public string Id { get; } = profile.Id;

    public string DisplayName { get; } = profile.DisplayName;

    public string ModelName { get; } = profile.ModelName;

    public ModelProviderKind Provider { get; } = profile.Provider;

    public string ProviderName => Provider switch
    {
        ModelProviderKind.Ollama => "Ollama",
        ModelProviderKind.OpenAiCompatible => "OpenAI compatible",
        ModelProviderKind.Anthropic => "Anthropic",
        _ => Provider.ToString()
    };

    public ModelSourceProfile Profile { get; } = profile;
}

public sealed class ModelSourcesViewModel : ViewModelBase
{
    private readonly IModelSourceCatalog _catalog;
    private readonly IUiTextProvider _text;
    private ModelSourceListItemViewModel? _selectedSource;
    private string? _editingId;
    private string _displayName = string.Empty;
    private ModelProviderKind _provider = ModelProviderKind.Ollama;
    private ModelProviderPreset _selectedPreset = ModelProviderPresets.Custom;
    private OpenAiTokenLimitParameter _selectedTokenLimitParameter = OpenAiTokenLimitParameter.MaxTokens;
    private string _endpoint = "http://127.0.0.1:11434";
    private string _modelName = "llama3";
    private int _maxOutputTokens = ModelSource.DefaultMaxOutputTokens;
    private int _maxSourceCharactersPerRequest = ModelSource.DefaultMaxSourceCharactersPerRequest;
    private string? _credentialReference;
    private string? _credentialFingerprint;
    private bool _applyingPresetDefaults;
    private bool _isDirty;
    private bool _suppressDirtyTracking;
    private ModelProviderKind? _pendingProvider;
    private ConnectionTestState _connectionState;
    private string? _connectionMessage;
    private AvailableModelInfo? _selectedAvailableModel;
    private long _editorRevision;
    private long _catalogRevision;
    private ModelCatalogState _catalogState;
    private string? _catalogMessage;

    public ModelSourcesViewModel(
        IModelSourceCatalog catalog,
        IUiTextProvider? text = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _text = text ?? FallbackUiTextProvider.Instance;
        TokenLimitParameterOptions =
        [
            new(
                OpenAiTokenLimitParameter.Omit,
                Text("ModelSourceTokenLimitParameterOmitOption", "Provider default (do not send)")),
            new(OpenAiTokenLimitParameter.MaxTokens, "max_tokens"),
            new(OpenAiTokenLimitParameter.MaxCompletionTokens, "max_completion_tokens")
        ];
        NewCommand = new RelayCommand(StartNew, () => !IsBusy);
        SaveCommand = new AsyncRelayCommand<string?>(SaveAsync, _ => CanSave);
        TestConnectionCommand = new AsyncRelayCommand<string?>(TestConnectionAsync, _ => CanTestConnection);
        RefreshModelsCommand = new AsyncRelayCommand<string?>(
            RefreshModelsAsync,
            _ => CanDiscoverModels && !IsBusy);
        ConfirmProviderChangeCommand = new RelayCommand(ConfirmProviderChange, () => PendingProvider is not null);
        CancelProviderChangeCommand = new RelayCommand(CancelProviderChange, () => PendingProvider is not null);
    }

    public event EventHandler? SecretInputConsumed;

    public ObservableCollection<ModelSourceListItemViewModel> Sources { get; } = [];

    public ObservableCollection<AvailableModelInfo> AvailableModels { get; } = [];

    public IReadOnlyList<ModelProviderKind> ProviderOptions { get; } =
        Enum.GetValues<ModelProviderKind>();

    public IReadOnlyList<ModelProviderPreset> PresetOptions { get; } = ModelProviderPresets.All;

    public IReadOnlyList<TokenLimitParameterOption> TokenLimitParameterOptions { get; }

    public IRelayCommand NewCommand { get; }

    public IAsyncRelayCommand<string?> SaveCommand { get; }

    public IAsyncRelayCommand<string?> TestConnectionCommand { get; }

    public IAsyncRelayCommand<string?> RefreshModelsCommand { get; }

    public IRelayCommand ConfirmProviderChangeCommand { get; }

    public IRelayCommand CancelProviderChangeCommand { get; }

    public ModelSourceListItemViewModel? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value) || value is null)
            {
                OnPropertyChanged(nameof(HasSelectedSource));
                return;
            }

            OnPropertyChanged(nameof(HasSelectedSource));
            LoadEditor(value.Profile);
        }
    }

    public bool HasSelectedSource => SelectedSource is not null;

    public string DisplayName
    {
        get => _displayName;
        set => SetEditorProperty(ref _displayName, value);
    }

    public ModelProviderKind Provider
    {
        get => _provider;
        set
        {
            if (_provider == value)
            {
                return;
            }

            if (!_suppressDirtyTracking && IsDirty)
            {
                PendingProvider = value;
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _provider, value))
            {
                MarkDirty();
                OnPropertyChanged(nameof(IsApiKeyRequired));
                OnPropertyChanged(nameof(IsOllama));
                OnPropertyChanged(nameof(IsOpenAiCompatible));
                OnPropertyChanged(nameof(CanDiscoverModels));
                OnPropertyChanged(nameof(SendsOutputTokenBudget));
                NotifyPresetProperties();
                if (value != ModelProviderKind.OpenAiCompatible)
                {
                    SetPresetWithoutApplyingDefaults(ModelProviderPresets.Custom);
                }

                InvalidateModelCatalog();
            }
        }
    }

    public ModelProviderPreset SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetProperty(ref _selectedPreset, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedPresetId));
            NotifyPresetProperties();
            if (_suppressDirtyTracking)
            {
                return;
            }

            MarkDirty();
            InvalidateModelCatalog();
            if (Provider == ModelProviderKind.OpenAiCompatible)
            {
                _applyingPresetDefaults = true;
                try
                {
                    SelectedTokenLimitParameter = value.DefaultTokenLimitParameter;
                    if (!value.IsCustom)
                    {
                        DisplayName = value.DisplayName;
                        Endpoint = value.DefaultEndpoint?.AbsoluteUri ?? Endpoint;
                        ModelName = value.DefaultModelName ?? ModelName;
                    }
                }
                finally
                {
                    _applyingPresetDefaults = false;
                }
            }
        }
    }

    /// <summary>
    /// Stable value used by the WinUI selector. Binding by ID avoids relying on object identity when the
    /// selector materializes or restores its items.
    /// </summary>
    public string SelectedPresetId
    {
        get => SelectedPreset.Id;
        set
        {
            if (ModelProviderPresets.TryGet(value, out var preset))
            {
                SelectedPreset = preset;
            }
        }
    }

    public OpenAiTokenLimitParameter SelectedTokenLimitParameter
    {
        get => _selectedTokenLimitParameter;
        set
        {
            if (SetProperty(ref _selectedTokenLimitParameter, value))
            {
                OnPropertyChanged(nameof(SelectedTokenLimitParameterOption));
                OnPropertyChanged(nameof(SendsOutputTokenBudget));
                MarkDirty();
            }
        }
    }

    /// <summary>
    /// Object-backed selection used by WinUI. This keeps the ComboBox selection visible for enum values and
    /// exposes the explicit omit option without a nullable SelectedValue conversion.
    /// </summary>
    public TokenLimitParameterOption SelectedTokenLimitParameterOption
    {
        get => TokenLimitParameterOptions.First(option => option.Value == SelectedTokenLimitParameter);
        set
        {
            if (value is not null)
            {
                SelectedTokenLimitParameter = value.Value;
            }
        }
    }

    public string Endpoint
    {
        get => _endpoint;
        set
        {
            if (SetProperty(ref _endpoint, value))
            {
                MarkDirty();
                InvalidateModelCatalog();
                ReconcilePresetWithEndpoint(value);
            }
        }
    }

    public string ModelName
    {
        get => _modelName;
        set => SetEditorProperty(ref _modelName, value);
    }

    public int MaxOutputTokens
    {
        get => _maxOutputTokens;
        set
        {
            if (SetProperty(ref _maxOutputTokens, value))
            {
                MarkDirty();
            }
        }
    }

    public int MaxSourceCharactersPerRequest
    {
        get => _maxSourceCharactersPerRequest;
        set
        {
            if (SetProperty(ref _maxSourceCharactersPerRequest, value))
            {
                MarkDirty();
            }
        }
    }

    public string CredentialReference => string.IsNullOrWhiteSpace(_credentialReference)
        ? Text("CredentialNotStored", "Not stored")
        : string.IsNullOrWhiteSpace(_credentialFingerprint)
            ? _credentialReference
            : $"{_credentialReference} · {_credentialFingerprint}";

    public bool IsApiKeyRequired => Provider != ModelProviderKind.Ollama;

    public bool IsOllama => Provider == ModelProviderKind.Ollama;

    public bool IsOpenAiCompatible => Provider == ModelProviderKind.OpenAiCompatible;

    public bool CanDiscoverModels => Provider is
        ModelProviderKind.Ollama or
        ModelProviderKind.OpenAiCompatible;

    public bool SendsOutputTokenBudget =>
        Provider != ModelProviderKind.OpenAiCompatible ||
        SelectedTokenLimitParameter != OpenAiTokenLimitParameter.Omit;

    public ModelCatalogState CatalogState
    {
        get => _catalogState;
        private set
        {
            if (SetProperty(ref _catalogState, value))
            {
                OnPropertyChanged(nameof(IsCatalogLoading));
                OnPropertyChanged(nameof(IsCatalogFailure));
            }
        }
    }

    public bool IsCatalogLoading => CatalogState == ModelCatalogState.Loading;

    public bool IsCatalogFailure => CatalogState == ModelCatalogState.Failed;

    public string? CatalogMessage
    {
        get => _catalogMessage;
        private set
        {
            if (SetProperty(ref _catalogMessage, value))
            {
                OnPropertyChanged(nameof(HasCatalogMessage));
            }
        }
    }

    public bool HasCatalogMessage => !string.IsNullOrWhiteSpace(CatalogMessage);

    public Uri? PresetDocumentationUri => SelectedPreset.DocumentationUri;

    public bool HasPresetDocumentation =>
        IsOpenAiCompatible && PresetDocumentationUri is not null;

    public string PresetEndpointHint => SelectedPreset.Id switch
    {
        ModelProviderPresets.DeepSeekId => Text(
            "ModelPresetDeepSeekHint",
            "DeepSeek accepts the host base URL; /v1 and full chat/completions inputs are normalized."),
        ModelProviderPresets.QwenId => Text(
            "ModelPresetQwenHint",
            "The default is China pay-as-you-go. Replace it with the matching workspace, region, or plan endpoint when needed."),
        ModelProviderPresets.XiaomiMimoId => Text(
            "ModelPresetXiaomiMimoHint",
            "For Xiaomi MiMo Token Plan, replace the endpoint with https://token-plan-cn.xiaomimimo.com/v1."),
        ModelProviderPresets.MiniMaxId => Text(
            "ModelPresetMiniMaxHint",
            "MiniMax uses the OpenAI-compatible /v1 API. The model suggestion remains editable."),
        ModelProviderPresets.DoubaoId => Text(
            "ModelPresetDoubaoHint",
            "The default is the Beijing ModelArk API. Keep the endpoint ID or regional route editable for your account."),
        ModelProviderPresets.ZhipuGlmId => Text(
            "ModelPresetZhipuGlmHint",
            "The default is Zhipu's general API. Coding Plan and other products may use a different editable endpoint."),
        ModelProviderPresets.KimiId => Text(
            "ModelPresetKimiHint",
            "The default is Kimi's China API. For the international platform, use https://api.moonshot.ai/v1."),
        ModelProviderPresets.OpenAiId => Text(
            "ModelPresetOpenAiHint",
            "OpenAI uses the /v1 API. The model suggestion remains editable."),
        _ => Text(
            "ModelPresetCustomHint",
            "Enter any HTTPS OpenAI-compatible base URL and model name; both remain editable.")
    };

    public AvailableModelInfo? SelectedAvailableModel
    {
        get => _selectedAvailableModel;
        set
        {
            if (SetProperty(ref _selectedAvailableModel, value) && value is not null)
            {
                ModelName = value.Name;
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                NotifyCommands();
            }
        }
    }

    public ModelProviderKind? PendingProvider
    {
        get => _pendingProvider;
        private set
        {
            if (SetProperty(ref _pendingProvider, value))
            {
                OnPropertyChanged(nameof(HasPendingProviderChange));
                ConfirmProviderChangeCommand.NotifyCanExecuteChanged();
                CancelProviderChangeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasPendingProviderChange => PendingProvider is not null;

    public ConnectionTestState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (SetProperty(ref _connectionState, value))
            {
                OnPropertyChanged(nameof(IsTestingConnection));
                OnPropertyChanged(nameof(IsConnectionFailure));
                OnPropertyChanged(nameof(HasNonErrorConnectionMessage));
                NotifyCommands();
            }
        }
    }

    public bool IsTestingConnection => ConnectionState == ConnectionTestState.Testing;

    public bool IsConnectionFailure =>
        ConnectionState == ConnectionTestState.Failed && HasConnectionMessage;

    public bool HasNonErrorConnectionMessage => HasConnectionMessage && !IsConnectionFailure;

    public string? ConnectionMessage
    {
        get => _connectionMessage;
        private set
        {
            if (SetProperty(ref _connectionMessage, value))
            {
                OnPropertyChanged(nameof(HasConnectionMessage));
                OnPropertyChanged(nameof(IsConnectionFailure));
                OnPropertyChanged(nameof(HasNonErrorConnectionMessage));
            }
        }
    }

    public bool HasConnectionMessage => !string.IsNullOrWhiteSpace(ConnectionMessage);

    public bool CanSave => !IsBusy && !HasPendingProviderChange;

    public bool CanTestConnection => !IsBusy && !HasPendingProviderChange && !IsTestingConnection;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        NotifyCommands();
        try
        {
            var profiles = await _catalog.GetAllAsync(cancellationToken).ConfigureAwait(true);
            Sources.Clear();
            foreach (var profile in profiles.OrderBy(static profile => profile.DisplayName, StringComparer.CurrentCulture))
            {
                Sources.Add(new ModelSourceListItemViewModel(profile));
            }

            if (Sources.Count > 0)
            {
                SelectedSource = Sources[0];
            }
            else
            {
                StartNew();
            }

            ErrorMessage = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = Text("ModelSourcesLoadFailed", "Model sources could not be loaded: {0}", exception.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    public async Task SaveAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        if (!TryCreateDraft(out var draft))
        {
            return;
        }

        var secretCharacters = apiKey?.ToCharArray() ?? [];
        IsBusy = true;
        NotifyCommands();
        try
        {
            var saved = await _catalog
                .SaveAsync(draft, secretCharacters, cancellationToken)
                .ConfigureAwait(true);
            await ReloadAfterSaveAsync(saved.Id, cancellationToken).ConfigureAwait(true);
            ErrorMessage = null;
            StatusMessage = Text(
                "ModelSourceSaved",
                "Model source saved. The API key was moved to Windows Credential Manager.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = Text("ModelSourceSaveFailed", "Model source could not be saved: {0}", exception.Message);
            StatusMessage = null;
        }
        finally
        {
            ClearSecret(secretCharacters);
            SecretInputConsumed?.Invoke(this, EventArgs.Empty);
            IsBusy = false;
            NotifyCommands();
        }
    }

    public async Task TestConnectionAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        if (!TryCreateDraft(out var draft))
        {
            return;
        }

        var secretCharacters = apiKey?.ToCharArray() ?? [];
        var editorRevision = _editorRevision;
        IsBusy = true;
        ConnectionState = ConnectionTestState.Testing;
        ConnectionMessage = Text("ModelConnectionTesting", "Testing connection…");
        NotifyCommands();
        try
        {
            var result = await _catalog
                .TestConnectionAsync(draft, secretCharacters, cancellationToken)
                .ConfigureAwait(true);
            if (_editorRevision != editorRevision)
            {
                return;
            }

            ConnectionState = result.IsSuccessful
                ? ConnectionTestState.Successful
                : ConnectionTestState.Failed;
            ConnectionMessage = result.IsSuccessful
                ? Text("ModelConnectionSucceeded", "Connection succeeded: {0}", result.Message)
                : Text("ModelConnectionRejected", "Connection failed: {0}", result.Message);
        }
        catch (OperationCanceledException)
        {
            if (_editorRevision == editorRevision)
            {
                ConnectionState = ConnectionTestState.NotTested;
                ConnectionMessage = null;
            }

            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_editorRevision == editorRevision)
            {
                ConnectionState = ConnectionTestState.Failed;
                ConnectionMessage = Text("ModelConnectionFailed", "Connection failed: {0}", exception.Message);
            }
        }
        finally
        {
            ClearSecret(secretCharacters);
            SecretInputConsumed?.Invoke(this, EventArgs.Empty);
            IsBusy = false;
            NotifyCommands();
        }
    }

    public async Task RefreshModelsAsync(
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        if (!CanDiscoverModels || !TryCreateCatalogDraft(out var draft))
        {
            return;
        }

        char[] secretCharacters = apiKey?.ToCharArray() ?? [];
        long catalogRevision = _catalogRevision;
        IsBusy = true;
        CatalogState = ModelCatalogState.Loading;
        CatalogMessage = Text("ModelCatalogLoading", "Refreshing provider-reported models…");
        NotifyCommands();
        try
        {
            var models = await _catalog
                .ListAvailableModelsAsync(draft, secretCharacters, cancellationToken)
                .ConfigureAwait(true);
            if (_catalogRevision != catalogRevision)
            {
                return;
            }

            AvailableModels.Clear();
            foreach (var model in models)
            {
                AvailableModels.Add(model);
            }

            SelectedAvailableModel = AvailableModels.FirstOrDefault(model =>
                string.Equals(model.Name, ModelName, StringComparison.OrdinalIgnoreCase));
            CatalogState = models.Count == 0
                ? ModelCatalogState.Empty
                : ModelCatalogState.Fresh;
            CatalogMessage = models.Count == 0
                ? Text(
                    "ModelCatalogEmpty",
                    "The provider reported no models. Manual entry remains available.")
                : Text("ModelCatalogFound", "Found {0} provider-reported model(s).", models.Count);
            ErrorMessage = null;
        }
        catch (ModelServiceException exception) when (exception.StatusCode is
            HttpStatusCode.NotFound or
            HttpStatusCode.MethodNotAllowed or
            HttpStatusCode.NotImplemented)
        {
            if (_catalogRevision == catalogRevision)
            {
                CatalogState = ModelCatalogState.Unsupported;
                CatalogMessage = Text(
                    "ModelCatalogUnsupported",
                    "This endpoint does not expose a compatible model list. You can keep the manual model name.");
            }
        }
        catch (OperationCanceledException)
        {
            if (_catalogRevision == catalogRevision)
            {
                CatalogState = ModelCatalogState.NotLoaded;
                CatalogMessage = null;
            }

            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_catalogRevision == catalogRevision)
            {
                // A catalog failure never destroys a manually entered model name or a successful connection test.
                CatalogState = ModelCatalogState.Failed;
                CatalogMessage = Text(
                    "ModelCatalogRefreshFailed",
                    "Models could not be refreshed: {0}. You can keep the manual model name.",
                    exception.Message);
            }
        }
        finally
        {
            ClearSecret(secretCharacters);
            SecretInputConsumed?.Invoke(this, EventArgs.Empty);
            IsBusy = false;
            NotifyCommands();
        }
    }

    public Task RefreshModelsAsync(CancellationToken cancellationToken = default) =>
        RefreshModelsAsync(apiKey: null, cancellationToken);

    public async Task DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSource is null || IsBusy)
        {
            return;
        }

        var sourceId = SelectedSource.Id;
        IsBusy = true;
        NotifyCommands();
        try
        {
            if (await _catalog.DeleteAsync(sourceId, cancellationToken).ConfigureAwait(true))
            {
                Sources.Remove(SelectedSource);
                if (Sources.Count > 0)
                {
                    SelectedSource = Sources[0];
                }
                else
                {
                    StartNew();
                }

                StatusMessage = Text(
                    "ModelSourceDeleted",
                    "Model source and its stored credential were deleted.");
                ErrorMessage = null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = Text("ModelSourceDeleteFailed", "Model source could not be deleted: {0}", exception.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private void StartNew()
    {
        _editorRevision++;
        _catalogRevision++;
        _selectedSource = null;
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(HasSelectedSource));
        _suppressDirtyTracking = true;
        _editingId = null;
        DisplayName = "Local Ollama";
        _provider = ModelProviderKind.Ollama;
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(IsApiKeyRequired));
        OnPropertyChanged(nameof(IsOllama));
        OnPropertyChanged(nameof(IsOpenAiCompatible));
        OnPropertyChanged(nameof(CanDiscoverModels));
        OnPropertyChanged(nameof(SendsOutputTokenBudget));
        SetPresetWithoutApplyingDefaults(ModelProviderPresets.Custom);
        SetTokenLimitParameterWithoutTracking(OpenAiTokenLimitParameter.MaxTokens);
        Endpoint = "http://127.0.0.1:11434";
        ModelName = "llama3";
        MaxOutputTokens = ModelSource.DefaultMaxOutputTokens;
        MaxSourceCharactersPerRequest = ModelSource.DefaultMaxSourceCharactersPerRequest;
        _credentialReference = null;
        _credentialFingerprint = null;
        OnPropertyChanged(nameof(CredentialReference));
        _suppressDirtyTracking = false;
        IsDirty = false;
        PendingProvider = null;
        ConnectionState = ConnectionTestState.NotTested;
        ConnectionMessage = null;
        AvailableModels.Clear();
        SelectedAvailableModel = null;
        CatalogState = ModelCatalogState.NotLoaded;
        CatalogMessage = null;
        ErrorMessage = null;
    }

    private void LoadEditor(ModelSourceProfile profile)
    {
        _editorRevision++;
        _catalogRevision++;
        _suppressDirtyTracking = true;
        _editingId = profile.Id;
        DisplayName = profile.DisplayName;
        _provider = profile.Provider;
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(IsApiKeyRequired));
        OnPropertyChanged(nameof(IsOllama));
        OnPropertyChanged(nameof(IsOpenAiCompatible));
        OnPropertyChanged(nameof(CanDiscoverModels));
        OnPropertyChanged(nameof(SendsOutputTokenBudget));
        var configuredPreset = ModelProviderPresets.ResolveOrCustom(profile.PresetId);
        var preset = ModelProviderPresets.ResolveEffective(
            profile.Provider,
            configuredPreset.Id,
            new Uri(profile.Endpoint, UriKind.Absolute));
        SetPresetWithoutApplyingDefaults(preset);
        SetTokenLimitParameterWithoutTracking(
            profile.TokenLimitParameter ?? configuredPreset.DefaultTokenLimitParameter);
        RefreshModelsCommand.NotifyCanExecuteChanged();
        Endpoint = profile.Endpoint;
        ModelName = profile.ModelName;
        MaxOutputTokens = profile.MaxOutputTokens ?? ModelSource.DefaultMaxOutputTokens;
        MaxSourceCharactersPerRequest = profile.MaxSourceCharactersPerRequest ??
            ModelSource.DefaultMaxSourceCharactersPerRequest;
        _credentialReference = profile.CredentialReference;
        _credentialFingerprint = profile.CredentialFingerprint;
        OnPropertyChanged(nameof(CredentialReference));
        _suppressDirtyTracking = false;
        IsDirty = false;
        PendingProvider = null;
        ConnectionState = ConnectionTestState.NotTested;
        ConnectionMessage = null;
        AvailableModels.Clear();
        SelectedAvailableModel = null;
        CatalogState = ModelCatalogState.NotLoaded;
        CatalogMessage = null;
        ErrorMessage = null;
    }

    private void ConfirmProviderChange()
    {
        if (PendingProvider is not { } provider)
        {
            return;
        }

        PendingProvider = null;
        _provider = provider;
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(IsApiKeyRequired));
        OnPropertyChanged(nameof(IsOllama));
        OnPropertyChanged(nameof(IsOpenAiCompatible));
        OnPropertyChanged(nameof(CanDiscoverModels));
        OnPropertyChanged(nameof(SendsOutputTokenBudget));
        NotifyPresetProperties();
        if (provider != ModelProviderKind.OpenAiCompatible)
        {
            SetPresetWithoutApplyingDefaults(ModelProviderPresets.Custom);
        }

        InvalidateModelCatalog();
        MarkDirty();
    }

    private void CancelProviderChange()
    {
        PendingProvider = null;
        OnPropertyChanged(nameof(Provider));
    }

    private bool TryCreateDraft(out ModelSourceDraft draft)
    {
        draft = null!;
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(DisplayName) || string.IsNullOrWhiteSpace(ModelName))
        {
            ErrorMessage = Text("ModelSourceFieldsRequired", "Display name and model name are required.");
            return false;
        }

        if (!ValidateRequestBudgets())
        {
            return false;
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            ErrorMessage = Text("ModelSourceAddressInvalid", "Enter an absolute HTTP or HTTPS service address.");
            return false;
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
        {
            ErrorMessage = Text(
                "ModelSourceHttpsRequired",
                "Remote model sources must use HTTPS; HTTP is allowed only for loopback services.");
            return false;
        }

        if (Provider == ModelProviderKind.OpenAiCompatible &&
            endpoint.Scheme == Uri.UriSchemeHttp &&
            !ModelProviderPresets.IsSupportedCustomLoopbackEndpoint(endpoint))
        {
            ErrorMessage = Text(
                "ModelSourceLoopbackOpenAiV1Required",
                "Loopback HTTP OpenAI-compatible sources must use an explicit /v1 base address.");
            return false;
        }

        var effectivePreset = ModelProviderPresets.ResolveEffective(Provider, SelectedPreset.Id, endpoint);

        draft = new ModelSourceDraft(
            _editingId,
            DisplayName.Trim(),
            Provider,
            endpoint,
            ModelName.Trim(),
            _credentialReference,
            Provider == ModelProviderKind.OpenAiCompatible
                ? effectivePreset.Id
                : ModelProviderPresets.CustomId,
            Provider == ModelProviderKind.OpenAiCompatible
                ? SelectedTokenLimitParameter
                : null,
            MaxOutputTokens,
            MaxSourceCharactersPerRequest);
        return true;
    }

    private bool TryCreateCatalogDraft(out ModelSourceDraft draft)
    {
        draft = null!;
        ErrorMessage = null;
        if (!ValidateRequestBudgets())
        {
            return false;
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            ErrorMessage = Text("ModelSourceAddressInvalid", "Enter an absolute HTTP or HTTPS service address.");
            return false;
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
        {
            ErrorMessage = Text(
                "ModelSourceHttpsRequired",
                "Remote model sources must use HTTPS; HTTP is allowed only for loopback services.");
            return false;
        }

        if (Provider == ModelProviderKind.OpenAiCompatible &&
            endpoint.Scheme == Uri.UriSchemeHttp &&
            !ModelProviderPresets.IsSupportedCustomLoopbackEndpoint(endpoint))
        {
            ErrorMessage = Text(
                "ModelSourceLoopbackOpenAiV1Required",
                "Loopback HTTP OpenAI-compatible sources must use an explicit /v1 base address.");
            return false;
        }

        ModelProviderPreset effectivePreset = ModelProviderPresets.ResolveEffective(
            Provider,
            SelectedPreset.Id,
            endpoint);
        string modelName = string.IsNullOrWhiteSpace(ModelName)
            ? effectivePreset.DefaultModelName ?? "catalog-discovery"
            : ModelName.Trim();
        draft = new ModelSourceDraft(
            _editingId,
            string.IsNullOrWhiteSpace(DisplayName) ? "Model catalog" : DisplayName.Trim(),
            Provider,
            endpoint,
            modelName,
            _credentialReference,
            Provider == ModelProviderKind.OpenAiCompatible
                ? effectivePreset.Id
                : ModelProviderPresets.CustomId,
            Provider == ModelProviderKind.OpenAiCompatible
                ? SelectedTokenLimitParameter
                : null,
            MaxOutputTokens,
            MaxSourceCharactersPerRequest);
        return true;
    }

    private bool ValidateRequestBudgets()
    {
        if (MaxOutputTokens is < ModelSource.MinimumMaxOutputTokens or > ModelSource.MaximumMaxOutputTokens)
        {
            ErrorMessage = Text(
                "ModelSourceMaxOutputTokensInvalid",
                "Response tokens must be between {0} and {1}.",
                ModelSource.MinimumMaxOutputTokens,
                ModelSource.MaximumMaxOutputTokens);
            return false;
        }

        if (MaxSourceCharactersPerRequest is
            < ModelSource.MinimumMaxSourceCharactersPerRequest or
            > ModelSource.MaximumMaxSourceCharactersPerRequest)
        {
            ErrorMessage = Text(
                "ModelSourceBatchCharactersInvalid",
                "Source characters per translation batch must be between {0} and {1}.",
                ModelSource.MinimumMaxSourceCharactersPerRequest,
                ModelSource.MaximumMaxSourceCharactersPerRequest);
            return false;
        }

        return true;
    }

    private void SetPresetWithoutApplyingDefaults(ModelProviderPreset preset)
    {
        if (_selectedPreset == preset)
        {
            NotifyPresetProperties();
            return;
        }

        _selectedPreset = preset;
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(nameof(SelectedPresetId));
        NotifyPresetProperties();
    }

    private void SetTokenLimitParameterWithoutTracking(OpenAiTokenLimitParameter parameter)
    {
        if (_selectedTokenLimitParameter == parameter)
        {
            return;
        }

        _selectedTokenLimitParameter = parameter;
        OnPropertyChanged(nameof(SelectedTokenLimitParameter));
        OnPropertyChanged(nameof(SelectedTokenLimitParameterOption));
        OnPropertyChanged(nameof(SendsOutputTokenBudget));
    }

    private void ReconcilePresetWithEndpoint(string endpointText)
    {
        if (_suppressDirtyTracking ||
            _applyingPresetDefaults ||
            Provider != ModelProviderKind.OpenAiCompatible ||
            SelectedPreset.IsCustom ||
            !Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        var effective = ModelProviderPresets.ResolveEffective(Provider, SelectedPreset.Id, endpoint);
        if (effective.IsCustom)
        {
            SetPresetWithoutApplyingDefaults(ModelProviderPresets.Custom);
        }
    }

    private void NotifyPresetProperties()
    {
        OnPropertyChanged(nameof(PresetDocumentationUri));
        OnPropertyChanged(nameof(HasPresetDocumentation));
        OnPropertyChanged(nameof(PresetEndpointHint));
    }

    private async Task ReloadAfterSaveAsync(string selectedId, CancellationToken cancellationToken)
    {
        var profiles = await _catalog.GetAllAsync(cancellationToken).ConfigureAwait(true);
        Sources.Clear();
        foreach (var profile in profiles.OrderBy(static profile => profile.DisplayName, StringComparer.CurrentCulture))
        {
            Sources.Add(new ModelSourceListItemViewModel(profile));
        }

        SelectedSource = Sources.First(source => source.Id == selectedId);
    }

    private void SetEditorProperty(ref string field, string value)
    {
        if (SetProperty(ref field, value))
        {
            MarkDirty();
        }
    }

    private void MarkDirty()
    {
        if (!_suppressDirtyTracking)
        {
            _editorRevision++;
            IsDirty = true;
            ConnectionState = ConnectionTestState.NotTested;
            ConnectionMessage = null;
        }
    }

    private void InvalidateModelCatalog()
    {
        _catalogRevision++;
        AvailableModels.Clear();
        SelectedAvailableModel = null;
        CatalogState = ModelCatalogState.NotLoaded;
        CatalogMessage = null;
        RefreshModelsCommand.NotifyCanExecuteChanged();
    }

    private static void ClearSecret(char[] characters)
    {
        if (characters.Length > 0)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }

    private void NotifyCommands()
    {
        NewCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        TestConnectionCommand.NotifyCanExecuteChanged();
        RefreshModelsCommand.NotifyCanExecuteChanged();
    }

    private string Text(string key, string fallback, params object?[] arguments) =>
        _text.GetText(key, fallback, arguments);
}
