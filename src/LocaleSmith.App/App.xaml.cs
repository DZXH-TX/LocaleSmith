using LocaleSmith.App.Services;
using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Services;
using LocaleSmith.Archive;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Services;
using LocaleSmith.Infrastructure.Cli;
using LocaleSmith.Infrastructure.Environment;
using LocaleSmith.Infrastructure.Models;
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
using Microsoft.Windows.Globalization;

namespace LocaleSmith.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private IHost? _host;

    public App()
    {
        InitializeComponent();
    }

    public static IServiceProvider Services =>
        ((App)Current)._host?.Services
        ?? throw new InvalidOperationException("The application host has not started.");

    public static MainWindow? MainWindow { get; private set; }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var currentLocalAppData = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(currentLocalAppData))
            {
                throw new InvalidOperationException("The per-user application-data directory is unavailable.");
            }

            var currentAppDataRoot = Path.Combine(Path.GetFullPath(currentLocalAppData), "LocaleSmith");
            var currentCredentialStore = new WindowsCredentialSecretStore("LocaleSmith");
            var legacyCredentialStore = new WindowsCredentialSecretStore("JaxI18n");
            var legacyAppDataRoots = await LegacyAppDataMigrationCoordinator
                .MigrateAsync(
                    currentAppDataRoot,
                    legacyCredentialStore,
                    currentCredentialStore)
                .ConfigureAwait(true);
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
            // This unpackaged app must set its process-wide MRT Core language before DI resolves
            // WinUiTextProvider or MainWindow and their localized resources are constructed.
            ApplicationLanguages.PrimaryLanguageOverride = configuration.Language;
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

    private static IHost BuildHost(
        string appDataRoot,
        WindowsCredentialSecretStore currentCredentialStore,
        WindowsCredentialSecretStore legacyCredentialStore,
        IReadOnlyList<string> legacyTranslationMemoryPaths)
    {
        var builder = Host.CreateApplicationBuilder();
        var configPath = Path.Combine(
            appDataRoot,
            LegacyAppDataMigrator.CurrentConfigurationFileName);
        var translationMemoryPath = Path.Combine(appDataRoot, "translation-memory");
        var auditPath = Path.Combine(appDataRoot, "logs", "cli-audit.jsonl");
        var defaultSandbox = CliSandboxDirectory.CreateUnderAppDataRoot(appDataRoot);

        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<AppMotionService>();
        builder.Services.AddSingleton(currentCredentialStore);
        builder.Services.AddSingleton<ISecretStore>(_ =>
            new MigratingSecretStore(currentCredentialStore, legacyCredentialStore));
        builder.Services.AddSingleton<IMasterKeyStore, CredentialManagerMasterKeyStore>();
        builder.Services.AddSingleton<IConfigurationStore<AppConfiguration>>(services =>
            new EncryptedJsonConfigurationStore<AppConfiguration>(
                configPath,
                LegacyAppDataMigrator.CurrentConfigurationPurpose,
                services.GetRequiredService<IMasterKeyStore>()));
        builder.Services.AddSingleton(SafeModelHttpClientFactory.Create());
        builder.Services.AddSingleton<ModelServiceRegistry>();
        builder.Services.AddSingleton<IModelServiceRegistry>(static services =>
            services.GetRequiredService<ModelServiceRegistry>());
        builder.Services.AddSingleton<SecureAppStateService>();
        builder.Services.AddSingleton<IAppConfigurationService>(static services =>
            services.GetRequiredService<SecureAppStateService>());
        builder.Services.AddSingleton<IOnboardingService>(static services =>
            services.GetRequiredService<SecureAppStateService>());
        builder.Services.AddSingleton<IModelSourceCatalog>(static services =>
            services.GetRequiredService<SecureAppStateService>());
        builder.Services.AddSingleton<IModelSelectionService>(static services =>
            services.GetRequiredService<SecureAppStateService>());

        builder.Services.AddSingleton<IArchiveWorkspaceBackend, ArchiveWorkspaceBackend>();
        builder.Services.AddSingleton<ITranslationMemoryStore>(_ =>
            new FileTranslationMemoryStore(translationMemoryPath, legacyTranslationMemoryPaths));
        builder.Services.AddSingleton<ITranslationEngine, ModelTranslationEngine>();
        builder.Services.AddSingleton<TranslationPipeline>();
        builder.Services.AddSingleton<IPipelineJobScheduler, PipelineJobScheduler>();
        builder.Services.AddSingleton<TranslationLogService>();
        builder.Services.AddSingleton<ITranslationQueueService, PipelineTranslationQueueService>();
        builder.Services.AddSingleton<IOutputPathStrategy, DefaultOutputPathStrategy>();
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
        builder.Services.AddSingleton(static services => new McpModelToolExecutor(
            services.GetRequiredService<ISystemPromptContextProvider>(),
            services.GetRequiredService<ICliCommandPolicy>()));
        builder.Services.AddSingleton<IModelAssistantService, ModelAssistantService>();
        builder.Services.AddSingleton(_ => new CliConfirmationViewModelFactory(
            _.GetRequiredService<ICliCommandPolicy>(),
            _.GetRequiredService<ICliApprovalService>(),
            _.GetRequiredService<ICliRunner>(),
            _.GetRequiredService<ITerminalEnvironmentDetector>(),
            auditPath,
            _.GetRequiredService<IUiTextProvider>()));
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<OnboardingViewModel>();
        builder.Services.AddSingleton<DashboardViewModel>();
        builder.Services.AddSingleton<AssistantViewModel>();
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
