using CommunityToolkit.Mvvm.Input;
using JaxI18n.Presentation.Abstractions;
using JaxI18n.Presentation.Models;

namespace JaxI18n.Presentation.ViewModels;

public sealed class ShellViewModel : ViewModelBase
{
    private readonly IAppConfigurationService _configurationService;
    private readonly IUiTextProvider _text;
    private ShellSection _currentSection;
    private bool _isNavigationAvailable;
    private AppThemePreference _theme;
    private string _language = "zh-CN";

    public ShellViewModel(
        IAppConfigurationService configurationService,
        IUiTextProvider? text = null)
    {
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _text = text ?? FallbackUiTextProvider.Instance;
        NavigateCommand = new RelayCommand<ShellSection>(Navigate, CanNavigate);
    }

    public event EventHandler<ShellSection>? NavigationRequested;

    public IRelayCommand<ShellSection> NavigateCommand { get; }

    public ShellSection CurrentSection
    {
        get => _currentSection;
        private set => SetProperty(ref _currentSection, value);
    }

    public bool IsNavigationAvailable
    {
        get => _isNavigationAvailable;
        private set
        {
            if (SetProperty(ref _isNavigationAvailable, value))
            {
                NavigateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public AppThemePreference Theme
    {
        get => _theme;
        private set => SetProperty(ref _theme, value);
    }

    public string Language
    {
        get => _language;
        private set => SetProperty(ref _language, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var configuration = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(true);
            Theme = configuration.Theme;
            Language = configuration.Language;
            IsNavigationAvailable = configuration.IsOnboardingComplete;
            Navigate(configuration.IsOnboardingComplete ? ShellSection.Dashboard : ShellSection.Onboarding);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = _text.GetText(
                "ShellSettingsLoadFailed",
                "Encrypted settings could not be loaded: {0}",
                exception.Message);
            IsNavigationAvailable = false;
            Navigate(ShellSection.Onboarding);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void CompleteOnboarding()
    {
        IsNavigationAvailable = true;
        Navigate(ShellSection.Dashboard);
    }

    private bool CanNavigate(ShellSection section) =>
        section == ShellSection.Onboarding || IsNavigationAvailable;

    private void Navigate(ShellSection section)
    {
        if (!CanNavigate(section))
        {
            return;
        }

        CurrentSection = section;
        NavigationRequested?.Invoke(this, section);
    }
}
