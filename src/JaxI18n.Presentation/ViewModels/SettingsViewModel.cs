using CommunityToolkit.Mvvm.Input;
using JaxI18n.Presentation.Abstractions;
using JaxI18n.Presentation.Models;

namespace JaxI18n.Presentation.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly IAppConfigurationService _configurationService;
    private readonly IUiTextProvider _text;
    private readonly ICliDiagnosticRequestFactory? _cliDiagnosticFactory;
    private AppConfiguration? _loadedConfiguration;
    private string _language = "zh-CN";
    private AppThemePreference _theme;
    private bool _forceAppAnimations;
    private string _workspacePath = string.Empty;
    private string _sandboxPath = string.Empty;

    public SettingsViewModel(
        IAppConfigurationService configurationService,
        IUiTextProvider? text = null,
        ICliDiagnosticRequestFactory? cliDiagnosticFactory = null)
    {
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _text = text ?? FallbackUiTextProvider.Instance;
        _cliDiagnosticFactory = cliDiagnosticFactory;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        OpenCliDiagnosticCommand = new AsyncRelayCommand(
            OpenCliDiagnosticAsync,
            () => _cliDiagnosticFactory is not null && !IsBusy);
    }

    public event EventHandler<CliConfirmationRequestedEventArgs>? CliConfirmationRequested;

    public IReadOnlyList<string> LanguageOptions { get; } = ["zh-CN", "en-US"];

    public IReadOnlyList<AppThemePreference> ThemeOptions { get; } =
        Enum.GetValues<AppThemePreference>();

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand OpenCliDiagnosticCommand { get; }

    public string Language
    {
        get => _language;
        set => SetProperty(ref _language, value);
    }

    public AppThemePreference Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    public bool ForceAppAnimations
    {
        get => _forceAppAnimations;
        set => SetProperty(ref _forceAppAnimations, value);
    }

    public string SandboxPath
    {
        get => _sandboxPath;
        set => SetProperty(ref _sandboxPath, value);
    }

    public string WorkspacePath
    {
        get => _workspacePath;
        set => SetProperty(ref _workspacePath, value);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            _loadedConfiguration = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(true);
            Language = _loadedConfiguration.Language;
            Theme = _loadedConfiguration.Theme;
            ForceAppAnimations = _loadedConfiguration.ForceAppAnimations;
            WorkspacePath = _loadedConfiguration.WorkspacePath;
            SandboxPath = _loadedConfiguration.SandboxPath;
            ErrorMessage = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = Text("SettingsLoadFailed", "Settings could not be loaded: {0}", exception.Message);
        }
        finally
        {
            IsBusy = false;
            SaveCommand.NotifyCanExecuteChanged();
            OpenCliDiagnosticCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task SaveAsync()
    {
        if (_loadedConfiguration is null)
        {
            ErrorMessage = Text("SettingsLoadBeforeSave", "Load settings before saving changes.");
            return;
        }

        if (string.IsNullOrWhiteSpace(WorkspacePath) || string.IsNullOrWhiteSpace(SandboxPath))
        {
            ErrorMessage = Text(
                "SettingsWorkspaceAndSandboxRequired",
                "Workspace and sandbox paths are required.");
            return;
        }

        IsBusy = true;
        SaveCommand.NotifyCanExecuteChanged();
        OpenCliDiagnosticCommand.NotifyCanExecuteChanged();
        try
        {
            var updated = _loadedConfiguration with
            {
                Language = Language,
                Theme = Theme,
                ForceAppAnimations = ForceAppAnimations,
                WorkspacePath = Path.GetFullPath(WorkspacePath),
                SandboxPath = Path.GetFullPath(SandboxPath)
            };
            await _configurationService.SaveAsync(updated).ConfigureAwait(true);
            _loadedConfiguration = updated;
            ErrorMessage = null;
            StatusMessage = Text("SettingsSaved", "Settings saved securely.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = Text("SettingsSaveFailed", "Settings could not be saved: {0}", exception.Message);
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
            SaveCommand.NotifyCanExecuteChanged();
            OpenCliDiagnosticCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task OpenCliDiagnosticAsync()
    {
        if (_cliDiagnosticFactory is null || string.IsNullOrWhiteSpace(SandboxPath))
        {
            ErrorMessage = Text("SettingsSandboxRequired", "A sandbox path is required.");
            return;
        }

        try
        {
            var confirmation = await _cliDiagnosticFactory
                .CreateAsync(SandboxPath)
                .ConfigureAwait(true);
            CliConfirmationRequested?.Invoke(
                this,
                new CliConfirmationRequestedEventArgs(confirmation));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = Text("CliDiagnosticCreateFailed", "The command review could not be opened: {0}", exception.Message);
        }
    }

    private string Text(string key, string fallback, params object?[] arguments) =>
        _text.GetText(key, fallback, arguments);
}

public sealed class CliConfirmationRequestedEventArgs(CliConfirmationViewModel confirmation) : EventArgs
{
    public CliConfirmationViewModel Confirmation { get; } =
        confirmation ?? throw new ArgumentNullException(nameof(confirmation));
}
