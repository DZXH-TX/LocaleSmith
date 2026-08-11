using CommunityToolkit.Mvvm.Input;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.Presentation.ViewModels;

public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly IAppConfigurationService _configurationService;
    private readonly IUiTextProvider _text;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateGate = new();
    private AppConfiguration? _loadedConfiguration;
    private string _language = AppDisplayLanguages.DefaultLanguage;
    private string _appliedLanguage = AppDisplayLanguages.DefaultLanguage;
    private AppThemePreference _theme;
    private bool _forceAppAnimations;
    private string _workspacePath = string.Empty;
    private string _sandboxPath = string.Empty;
    private string _logDirectoryPath = string.Empty;
    private long _changeVersion;
    private long _persistedVersion;
    private bool _loading;
    private volatile bool _disposed;

    public SettingsViewModel(
        IAppConfigurationService configurationService,
        IUiTextProvider? text = null)
    {
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _text = text ?? FallbackUiTextProvider.Instance;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
    }

    public IReadOnlyList<string> LanguageOptions { get; } = AppDisplayLanguages.Supported;

    public IReadOnlyList<AppThemePreference> ThemeOptions { get; } =
        Enum.GetValues<AppThemePreference>();

    public IAsyncRelayCommand SaveCommand { get; }

    public string Language
    {
        get => _language;
        set
        {
            if (SetProperty(ref _language, value))
            {
                MarkChanged();
                OnPropertyChanged(nameof(IsLanguageRestartRequired));
            }
        }
    }

    public bool IsLanguageRestartRequired =>
        _loadedConfiguration is not null &&
        !string.Equals(_language, _appliedLanguage, StringComparison.Ordinal);

    public AppThemePreference Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                MarkChanged();
            }
        }
    }

    public bool ForceAppAnimations
    {
        get => _forceAppAnimations;
        set
        {
            if (SetProperty(ref _forceAppAnimations, value))
            {
                MarkChanged();
            }
        }
    }

    public string SandboxPath
    {
        get => _sandboxPath;
        set
        {
            if (SetProperty(ref _sandboxPath, value))
            {
                MarkChanged();
            }
        }
    }

    public string WorkspacePath
    {
        get => _workspacePath;
        set
        {
            if (SetProperty(ref _workspacePath, value))
            {
                MarkChanged();
            }
        }
    }

    public string LogDirectoryPath
    {
        get => _logDirectoryPath;
        set
        {
            if (SetProperty(ref _logDirectoryPath, value))
            {
                MarkChanged();
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateGate)
        {
            if (_loadedConfiguration is not null)
            {
                return;
            }
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            lock (_stateGate)
            {
                if (_loadedConfiguration is not null)
                {
                    return;
                }

                _loading = true;
            }

            IsBusy = true;
            SaveCommand.NotifyCanExecuteChanged();
            try
            {
                var loaded = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(true);
                var loadedLanguage = AppDisplayLanguages.ResolveOrDefault(loaded.Language);
                Language = loadedLanguage;
                Theme = loaded.Theme;
                ForceAppAnimations = loaded.ForceAppAnimations;
                WorkspacePath = loaded.WorkspacePath;
                SandboxPath = loaded.SandboxPath;
                LogDirectoryPath = string.IsNullOrWhiteSpace(loaded.LogDirectoryPath)
                    ? AppConfiguration.GetDefaultLogDirectoryPath()
                    : loaded.LogDirectoryPath;
                lock (_stateGate)
                {
                    _loadedConfiguration = loaded;
                    _appliedLanguage = loadedLanguage;
                    _changeVersion = 0;
                    _persistedVersion = 0;
                }
                OnPropertyChanged(nameof(IsLanguageRestartRequired));

                ErrorMessage = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ErrorMessage = Text("SettingsLoadFailed", "Settings could not be loaded: {0}", exception.Message);
            }
            finally
            {
                lock (_stateGate)
                {
                    _loading = false;
                }

                IsBusy = false;
                SaveCommand.NotifyCanExecuteChanged();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Persists the latest valid settings snapshot without requiring the settings page to remain open.
    /// This is used during application shutdown so live-applied appearance changes are not lost.
    /// </summary>
    public async Task<bool> FlushPendingChangesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateGate)
        {
            if (_loadedConfiguration is null)
            {
                return false;
            }
        }

        try
        {
            var outcome = await PersistPendingChangesAsync(cancellationToken).ConfigureAwait(false);
            return outcome.Result is PendingSaveResult.NoChanges or PendingSaveResult.Saved;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SaveAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsBusy = true;
        SaveCommand.NotifyCanExecuteChanged();
        try
        {
            var outcome = await PersistPendingChangesAsync(CancellationToken.None).ConfigureAwait(true);
            if (outcome.Result is PendingSaveResult.NoChanges or PendingSaveResult.Saved)
            {
                ErrorMessage = null;
                StatusMessage = Text("SettingsSaved", "Settings saved securely.");
            }
            else
            {
                ErrorMessage = CreateValidationError(outcome);
                StatusMessage = null;
            }
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
        }
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Do not dispose a semaphore still owned by an in-flight load or save; its finally block
        // must be able to release safely even if host shutdown stopped waiting for that I/O.
        if (_operationGate.Wait(0))
        {
            _operationGate.Dispose();
        }
    }

    public void ReportLanguageRestartFailure(string detail)
    {
        ErrorMessage = Text(
            "SettingsRestartFailed",
            "The app could not restart to apply the display language: {0}",
            detail);
        StatusMessage = null;
    }

    public void ReportLanguageRestartBlockedByActiveTranslations()
    {
        ErrorMessage = Text(
            "SettingsRestartBlockedByActiveTranslations",
            "Wait for active translation jobs to finish or cancel them before restarting the app.");
        StatusMessage = null;
    }

    public bool TryGetPersistedDisplayLanguageForRestart(out string language)
    {
        lock (_stateGate)
        {
            language = _language;
            return _loadedConfiguration is not null &&
                _changeVersion == _persistedVersion &&
                !string.Equals(_language, _appliedLanguage, StringComparison.Ordinal);
        }
    }

    private void MarkChanged()
    {
        lock (_stateGate)
        {
            if (!_loading && _loadedConfiguration is not null)
            {
                _changeVersion++;
            }
        }
    }

    private bool TryCapturePendingConfiguration(
        out PendingConfiguration? pending,
        out PendingSaveOutcome outcome)
    {
        string language;
        AppThemePreference theme;
        bool forceAppAnimations;
        string workspacePath;
        string sandboxPath;
        string logDirectoryPath;
        long version;
        lock (_stateGate)
        {
            if (_loadedConfiguration is null)
            {
                pending = null;
                outcome = new PendingSaveOutcome(PendingSaveResult.NotLoaded);
                return false;
            }

            if (_changeVersion == _persistedVersion)
            {
                pending = null;
                outcome = new PendingSaveOutcome(PendingSaveResult.NoChanges);
                return true;
            }

            language = _language;
            theme = _theme;
            forceAppAnimations = _forceAppAnimations;
            workspacePath = _workspacePath;
            sandboxPath = _sandboxPath;
            logDirectoryPath = _logDirectoryPath;
            version = _changeVersion;
        }

        if (string.IsNullOrWhiteSpace(workspacePath)
            || string.IsNullOrWhiteSpace(sandboxPath)
            || string.IsNullOrWhiteSpace(logDirectoryPath))
        {
            pending = null;
            outcome = new PendingSaveOutcome(PendingSaveResult.RequiredPathsMissing);
            return false;
        }

        try
        {
            pending = new PendingConfiguration(
                new AppSettingsUpdate(
                    language,
                    theme,
                    forceAppAnimations,
                    Path.GetFullPath(workspacePath),
                    Path.GetFullPath(sandboxPath),
                    AppConfiguration.NormalizeLogDirectoryPath(logDirectoryPath)),
                version);
            outcome = new PendingSaveOutcome(PendingSaveResult.NoChanges);
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            pending = null;
            outcome = new PendingSaveOutcome(PendingSaveResult.InvalidPath, exception.Message);
            return false;
        }
    }

    private async Task<PendingSaveOutcome> PersistPendingChangesAsync(
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryCapturePendingConfiguration(out var pending, out var outcome))
            {
                return outcome;
            }

            if (pending is null)
            {
                return new PendingSaveOutcome(PendingSaveResult.NoChanges);
            }

            lock (_stateGate)
            {
                if (pending.Version <= _persistedVersion)
                {
                    return new PendingSaveOutcome(PendingSaveResult.NoChanges);
                }
            }

            await _configurationService
                .SaveSettingsAsync(pending.Settings, cancellationToken)
                .ConfigureAwait(false);
            lock (_stateGate)
            {
                if (pending.Version > _persistedVersion)
                {
                    _persistedVersion = pending.Version;
                }
            }

            return new PendingSaveOutcome(PendingSaveResult.Saved);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private string CreateValidationError(PendingSaveOutcome outcome) => outcome.Result switch
    {
        PendingSaveResult.NotLoaded =>
            Text("SettingsLoadBeforeSave", "Load settings before saving changes."),
        PendingSaveResult.RequiredPathsMissing =>
            Text(
                "SettingsWorkspaceSandboxAndLogRequired",
                "Workspace, sandbox, and log directory paths are required."),
        PendingSaveResult.InvalidPath =>
            Text(
                "SettingsSaveFailed",
                "Settings could not be saved: {0}",
                outcome.ErrorDetail ?? "Invalid path."),
        _ => throw new InvalidOperationException("A successful settings save does not have a validation error.")
    };

    private string Text(string key, string fallback, params object?[] arguments) =>
        _text.GetText(key, fallback, arguments);

    private sealed record PendingConfiguration(AppSettingsUpdate Settings, long Version);

    private sealed record PendingSaveOutcome(PendingSaveResult Result, string? ErrorDetail = null);

    private enum PendingSaveResult
    {
        NoChanges,
        Saved,
        NotLoaded,
        RequiredPathsMissing,
        InvalidPath
    }
}
