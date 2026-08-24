using System.Globalization;
using LocaleSmith.App.Services;
using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Services;
using LocaleSmith.Archive;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Services;
using LocaleSmith.Infrastructure.Cli;
using LocaleSmith.Infrastructure.Environment;
using LocaleSmith.Infrastructure.Models;
using LocaleSmith.Infrastructure.ModPlatform;
using LocaleSmith.Infrastructure.Security;
using LocaleSmith.Mcp;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.Services;
using LocaleSmith.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.Globalization;

namespace LocaleSmith.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private IHost? _host;
    private readonly ApplicationStorageScope _storageScope;
    private readonly string _bootstrapLanguage;

    public App()
    {
        _storageScope = ApplicationStorageScope.Detect();
        // The unpackaged app cannot rely on PrimaryLanguageOverride persisting between processes.
        // Read the non-sensitive bootstrap preference synchronously so MRT Core sees it before
        // App.xaml loads any XAML or ResourceLoader-backed content.
        _bootstrapLanguage = AppLanguageBootstrapper.Initialize(
            LoadBootstrapLanguageOrDefault(_storageScope.AppDataRoot),
            ApplyDisplayLanguage,
            InitializeComponent);
    }

    public static IServiceProvider Services =>
        ((App)Current)._host?.Services
        ?? throw new InvalidOperationException("The application host has not started.");

    public static MainWindow? MainWindow { get; private set; }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var currentAppDataRoot = _storageScope.AppDataRoot;
            var currentCredentialStore = new WindowsCredentialSecretStore(
                _storageScope.CredentialTargetPrefix);
            WindowsCredentialSecretStore? legacyCredentialStore = null;
            IReadOnlyList<string> legacyAppDataRoots = [];
            if (_storageScope.IsProduction)
            {
                legacyCredentialStore = new WindowsCredentialSecretStore("JaxI18n");
                legacyAppDataRoots = await LegacyAppDataMigrationCoordinator
                    .MigrateAsync(
                        currentAppDataRoot,
                        legacyCredentialStore,
                        currentCredentialStore)
                    .ConfigureAwait(true);
            }
            var legacyTranslationMemoryPaths = legacyAppDataRoots
                .Select(static root => Path.Combine(root, "translation-memory"))
                .ToArray();

            _host = BuildHost(
                currentAppDataRoot,
                currentCredentialStore,
                legacyCredentialStore,
                legacyTranslationMemoryPaths);
            await _host.StartAsync().ConfigureAwait(true);
            var state = _host.Services.GetRequiredService<SecureAppStateService>();
            await state.InitializeAsync().ConfigureAwait(true);
            var configuration = await state.LoadAsync().ConfigureAwait(true);
            var configuredLanguage = AppDisplayLanguages.ResolveOrDefault(configuration.Language);
            if (SynchronizeBootstrapLanguageBestEffort(currentAppDataRoot, configuredLanguage))
            {
                // This is the one-time upgrade/recovery path for a missing or stale bootstrap file.
                // A successful call terminates this process; a failure returns and we continue with
                // the best available late-applied language for the current launch.
                _ = AppInstance.Restart(string.Empty);
            }

            ApplyDisplayLanguage(configuredLanguage);
            _host.Services.GetRequiredService<AppMotionService>()
                .SetForceAppAnimations(configuration.ForceAppAnimations);
            MainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow.ApplyTheme(configuration.Theme);
            MainWindow.Closed += OnMainWindowClosed;
            MainWindow.Activate();
        }
        catch (Exception exception)
        {
            MainWindow = new MainWindow(exception.Message);
            MainWindow.Closed += OnMainWindowClosed;
            MainWindow.Activate();
        }
    }

    public static string RestartWithDisplayLanguage(string language)
    {
        AppLanguagePreferenceStore.Save(ApplicationStorageScope.Detect().AppDataRoot, language);
        return AppInstance.Restart(string.Empty).ToString();
    }

    private static void ApplyDisplayLanguage(string language)
    {
        ApplicationLanguages.PrimaryLanguageOverride = language;
        var uiCulture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentUICulture = uiCulture;
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
    }

    private static string LoadBootstrapLanguageOrDefault(string appDataRoot)
    {
        try
        {
            return AppLanguagePreferenceStore.LoadOrDefault(appDataRoot);
        }
        catch (InvalidOperationException)
        {
            return AppDisplayLanguages.DefaultLanguage;
        }
    }

    private bool SynchronizeBootstrapLanguageBestEffort(string appDataRoot, string language)
    {
        if (string.Equals(_bootstrapLanguage, language, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            AppLanguagePreferenceStore.Save(appDataRoot, language);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            // The encrypted configuration remains authoritative. A later explicit restart action
            // will surface bootstrap preference write failures to the user.
            return false;
        }
    }

    private static IHost BuildHost(
        string appDataRoot,
        WindowsCredentialSecretStore currentCredentialStore,
        WindowsCredentialSecretStore? legacyCredentialStore,
        IReadOnlyList<string> legacyTranslationMemoryPaths)
    {
        var builder = Host.CreateApplicationBuilder();
        var configPath = Path.Combine(
            appDataRoot,
            LegacyAppDataMigrator.CurrentConfigurationFileName);
        var translationMemoryPath = Path.Combine(appDataRoot, "translation-memory");
        var auditPath = Path.Combine(appDataRoot, "logs", "cli-audit.jsonl");
        var securityLockRoot = Path.Combine(appDataRoot, "SecurityLocks");
        var defaultSandbox = CliSandboxDirectory.CreateUnderAppDataRoot(appDataRoot);

        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<AppMotionService>();
        builder.Services.AddSingleton(currentCredentialStore);
        builder.Services.AddSingleton<ISecretStore>(_ => legacyCredentialStore is null
            ? currentCredentialStore
            : new MigratingSecretStore(
                currentCredentialStore,
                legacyCredentialStore,
                securityLockRoot));
        builder.Services.AddSingleton<IMasterKeyStore>(services =>
            new CredentialManagerMasterKeyStore(
                services.GetRequiredService<ISecretStore>(),
                securityLockRoot));
        builder.Services.AddSingleton(static services =>
            ModPlatformClient.CreateForApplication(services.GetRequiredService<ISecretStore>()));
        builder.Services.AddSingleton<IModPlatformClient>(static services =>
            services.GetRequiredService<ModPlatformClient>());
        builder.Services.AddSingleton<IModPlatformBillingClient>(static services =>
            services.GetRequiredService<ModPlatformClient>());
        builder.Services.AddSingleton(static _ => new WindowsMicrosoftStorefront(() => MainWindow));
        builder.Services.AddSingleton<IMicrosoftStorefront>(static services =>
            services.GetRequiredService<WindowsMicrosoftStorefront>());
        builder.Services.AddSingleton(static _ => ModPlatformArtifactDownloader.CreateForApplication());
        builder.Services.AddSingleton<IModPlatformArtifactDownloader>(static services =>
            services.GetRequiredService<ModPlatformArtifactDownloader>());
        builder.Services.AddSingleton(static _ =>
            ModPlatformAcceleratedArtifactDownloader.CreateForApplication());
        builder.Services.AddSingleton<IModPlatformAcceleratedArtifactDownloader>(static services =>
            services.GetRequiredService<ModPlatformAcceleratedArtifactDownloader>());
        builder.Services.AddSingleton<ModPlatformArtifactDownloadCoordinator>();
        builder.Services.AddSingleton<IModPlatformArtifactDownloadCoordinator>(static services =>
            services.GetRequiredService<ModPlatformArtifactDownloadCoordinator>());
        builder.Services.AddSingleton<SecretStoreModPlatformCredentialService>();
        builder.Services.AddSingleton<IModPlatformCredentialService>(static services =>
            services.GetRequiredService<SecretStoreModPlatformCredentialService>());
        builder.Services.AddSingleton<IConfigurationStore<AppConfiguration>>(services =>
            new EncryptedJsonConfigurationStore<AppConfiguration>(
                configPath,
                LegacyAppDataMigrator.CurrentConfigurationPurpose,
                services.GetRequiredService<IMasterKeyStore>()));
        builder.Services.AddSingleton(SafeModelHttpClientFactory.Create());
        builder.Services.AddSingleton<ModelServiceRegistry>();
        builder.Services.AddSingleton<IModelServiceRegistry>(static services =>
            services.GetRequiredService<ModelServiceRegistry>());
        builder.Services.AddSingleton(services => new SecureAppStateService(
            services.GetRequiredService<IConfigurationStore<AppConfiguration>>(),
            services.GetRequiredService<ISecretStore>(),
            services.GetRequiredService<ModelServiceRegistry>(),
            services.GetRequiredService<HttpClient>(),
            services.GetRequiredService<ICliSandboxRootManager>(),
            services.GetRequiredService<IAppLanguagePreferenceWriter>(),
            appDataRoot));
        builder.Services.AddSingleton<IAppConfigurationService>(static services =>
            services.GetRequiredService<SecureAppStateService>());
        builder.Services.AddSingleton<IAppDisplayLanguageService>(static services =>
            services.GetRequiredService<SecureAppStateService>());
        builder.Services.AddSingleton<IOnboardingService>(static services =>
            services.GetRequiredService<SecureAppStateService>());
        builder.Services.AddSingleton<IModelSourceCatalog>(static services =>
            services.GetRequiredService<SecureAppStateService>());
        builder.Services.AddSingleton<IModelSelectionService>(static services =>
            services.GetRequiredService<SecureAppStateService>());
        builder.Services.AddSingleton<IAppLanguagePreferenceWriter>(
            _ => new FileAppLanguagePreferenceWriter(appDataRoot));

        builder.Services.AddSingleton<IArchiveScanner, NativeArchiveScanner>();
        builder.Services.AddSingleton<IArchiveWorkspaceBackend>(static services =>
            new ArchiveWorkspaceBackend(services.GetRequiredService<IArchiveScanner>()));
        builder.Services.AddSingleton<ITranslationMemoryStore>(_ =>
            new FileTranslationMemoryStore(translationMemoryPath, legacyTranslationMemoryPaths));
        builder.Services.AddSingleton<ITranslationEngine, ModelTranslationEngine>();
        builder.Services.AddSingleton<TranslationPipeline>();
        builder.Services.AddSingleton<IPipelineJobScheduler, PipelineJobScheduler>();
        builder.Services.AddSingleton<TranslationLogService>();
        builder.Services.AddSingleton<ITranslationQueueService, PipelineTranslationQueueService>();
        builder.Services.AddSingleton<IOutputPathStrategy, DefaultOutputPathStrategy>();
        builder.Services.AddSingleton<IModProjectWorkspace, InMemoryModProjectWorkspace>();
        builder.Services.AddSingleton<IUiTextProvider, WinUiTextProvider>();
        builder.Services.AddSingleton<IUiDispatcher>(_ =>
            new DispatcherQueueUiDispatcher(
                DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("The UI dispatcher is unavailable.")));

        builder.Services.AddSingleton<ITerminalEnvironmentDetector, TerminalEnvironmentDetector>();
        builder.Services.AddSingleton<ISystemPromptContextProvider, SafeSystemPromptContextProvider>();
        builder.Services.AddSingleton<IPrivilegeContext, WindowsPrivilegeContext>();
        builder.Services.AddSingleton<ICliApprovalService, CliApprovalService>();
        builder.Services.AddSingleton<ICliAuditSink>(_ => new JsonLinesCliAuditSink(auditPath));
        builder.Services.AddSingleton(_ =>
        {
            var validatedSandbox = CliSandboxDirectory.ValidateExisting(appDataRoot, defaultSandbox);
            return new SafeCliCommandPolicy(
                TrustedCliExecutableDiscovery.FindInstalled(),
                [validatedSandbox],
                temporaryRoot: validatedSandbox,
                maximumTimeout: TimeSpan.FromSeconds(30));
        });
        builder.Services.AddSingleton<ICliCommandPolicy>(static services =>
            services.GetRequiredService<SafeCliCommandPolicy>());
        builder.Services.AddSingleton<ICliSandboxRootManager>(static services =>
            services.GetRequiredService<SafeCliCommandPolicy>());
        builder.Services.AddSingleton<ICliRunner, SafeCliRunner>();
        builder.Services.AddSingleton<ModelToolOrchestrator>();
        builder.Services.AddSingleton<IProjectMcpBackend, ProjectMcpBackend>();
        builder.Services.AddSingleton(static services => new McpModelToolExecutor(
            services.GetRequiredService<ISystemPromptContextProvider>(),
            services.GetRequiredService<ICliCommandPolicy>(),
            projectBackend: services.GetRequiredService<IProjectMcpBackend>()));
        builder.Services.AddSingleton<IModelAssistantService>(static services =>
            new ModelAssistantService(
                services.GetRequiredService<IModelServiceRegistry>(),
                services.GetRequiredService<ISystemPromptContextProvider>(),
                services.GetRequiredService<IAppConfigurationService>(),
                services.GetRequiredService<McpModelToolExecutor>(),
                services.GetRequiredService<ModelToolOrchestrator>()));
        builder.Services.AddSingleton(_ => new CliConfirmationViewModelFactory(
            _.GetRequiredService<ICliCommandPolicy>(),
            _.GetRequiredService<ICliApprovalService>(),
            _.GetRequiredService<ICliRunner>(),
            _.GetRequiredService<ITerminalEnvironmentDetector>(),
            auditPath,
            _.GetRequiredService<IUiTextProvider>()));
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton(services => new OnboardingViewModel(
            services.GetRequiredService<IOnboardingService>(),
            services.GetRequiredService<IUiTextProvider>(),
            services.GetRequiredService<IAppDisplayLanguageService>(),
            Path.Combine(appDataRoot, "CliSandbox"),
            Path.Combine(appDataRoot, "logs", "translations")));
        builder.Services.AddSingleton<DashboardViewModel>();
        builder.Services.AddSingleton<AssistantViewModel>();
        builder.Services.AddSingleton<CommunityViewModel>();
        builder.Services.AddSingleton<MicrosoftStoreBillingViewModel>();
        builder.Services.AddSingleton<ModArtifactDownloadViewModel>();
        builder.Services.AddSingleton<ModelSourcesViewModel>();
        builder.Services.AddSingleton<TranslationLogsViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();

        return builder.Build();
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (host is null)
        {
            return;
        }

        using (var flushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            try
            {
                host.Services
                    .GetRequiredService<SettingsViewModel>()
                    .FlushPendingChangesAsync(flushTimeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException)
            {
                // A stalled settings write must not keep the application process alive indefinitely.
            }
        }

        ShutdownHostAsync(host).GetAwaiter().GetResult();
    }

    private static async Task ShutdownHostAsync(IHost host)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await host.StopAsync(timeout.Token).ConfigureAwait(false);
        if (host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        else
        {
            host.Dispose();
        }
    }
}
