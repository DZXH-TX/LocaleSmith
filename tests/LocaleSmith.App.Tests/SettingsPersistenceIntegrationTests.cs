using LocaleSmith.App.Services;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Services;
using LocaleSmith.Infrastructure.Security;
using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.App.Tests;

public sealed class SettingsPersistenceIntegrationTests
{
    [Fact]
    public async Task ShutdownFlushRestoresLatestSettingsThroughFreshServiceAndStoreInstances()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "LocaleSmith.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var configurationPath = Path.Combine(testRoot, "settings.localesmithcfg");
            var firstWorkspace = Path.Combine(testRoot, "workspace-one");
            var secondWorkspace = Path.Combine(testRoot, "workspace-two");
            var sandbox = Path.Combine(testRoot, "sandbox");
            var firstLogDirectory = Path.Combine(testRoot, "logs-one");
            var secondLogDirectory = Path.Combine(testRoot, "logs-two");
            using var secrets = new InMemorySecretStore();
            var initial = new AppConfiguration
            {
                IsOnboardingComplete = true,
                WorkspacePath = firstWorkspace,
                SandboxPath = sandbox,
                LogDirectoryPath = firstLogDirectory,
                Language = "zh-CN",
                Theme = AppThemePreference.System,
                ForceAppAnimations = false
            };

            using (var firstStore = CreateStore(configurationPath, secrets))
            {
                await firstStore.SaveAsync(initial, cancellationToken);
                using var firstRegistry = new ModelServiceRegistry();
                using var firstHttpClient = new HttpClient(new RejectingHttpHandler());
                using var firstState = new SecureAppStateService(
                    firstStore,
                    secrets,
                    firstRegistry,
                    firstHttpClient,
                    new StubSandboxRootManager());
                using var settings = new SettingsViewModel(firstState);
                await settings.LoadAsync(cancellationToken);
                settings.Language = "en-US";
                settings.Theme = AppThemePreference.Dark;
                settings.ForceAppAnimations = true;
                settings.WorkspacePath = secondWorkspace;
                settings.LogDirectoryPath = secondLogDirectory;
                await firstState.SaveAsync(
                    new ModelSourceDraft(
                        null,
                        "Local model saved after settings opened",
                        LocaleSmith.Core.Models.ModelProviderKind.Ollama,
                        new Uri("http://127.0.0.1:11434"),
                        "llama3",
                        null),
                    ReadOnlyMemory<char>.Empty,
                    cancellationToken);

                Assert.True(await settings.FlushPendingChangesAsync(cancellationToken));
            }

            var encryptedBytes = await File.ReadAllTextAsync(configurationPath, cancellationToken);
            Assert.DoesNotContain(secondWorkspace, encryptedBytes, StringComparison.Ordinal);
            Assert.DoesNotContain(secondLogDirectory, encryptedBytes, StringComparison.Ordinal);

            using var secondStore = CreateStore(configurationPath, secrets);
            using var secondRegistry = new ModelServiceRegistry();
            using var secondHttpClient = new HttpClient(new RejectingHttpHandler());
            using var secondState = new SecureAppStateService(
                secondStore,
                secrets,
                secondRegistry,
                secondHttpClient,
                new StubSandboxRootManager());
            using var restoredSettings = new SettingsViewModel(secondState);

            await restoredSettings.LoadAsync(cancellationToken);

            Assert.Equal("en-US", restoredSettings.Language);
            Assert.Equal(AppThemePreference.Dark, restoredSettings.Theme);
            Assert.True(restoredSettings.ForceAppAnimations);
            Assert.Equal(secondWorkspace, restoredSettings.WorkspacePath);
            Assert.Equal(sandbox, restoredSettings.SandboxPath);
            Assert.Equal(secondLogDirectory, restoredSettings.LogDirectoryPath);
            var restoredConfiguration = await secondState.LoadAsync(cancellationToken);
            Assert.Equal(
                "Local model saved after settings opened",
                Assert.Single(restoredConfiguration.ModelSources).DisplayName);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ShutdownFlushLeavesLastPersistedSettingsWhenCurrentPathsAreInvalid()
    {
        var configuration = new RecordingConfigurationService(new AppConfiguration
        {
            IsOnboardingComplete = true,
            WorkspacePath = Path.Combine(Path.GetTempPath(), "LocaleSmith.Tests", "workspace"),
            SandboxPath = Path.Combine(Path.GetTempPath(), "LocaleSmith.Tests", "sandbox"),
            Theme = AppThemePreference.Light
        });
        using var settings = new SettingsViewModel(configuration);
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        settings.Theme = AppThemePreference.Dark;
        settings.LogDirectoryPath = string.Empty;

        var flushed = await settings.FlushPendingChangesAsync(TestContext.Current.CancellationToken);

        Assert.False(flushed);
        Assert.Equal(AppThemePreference.Light, configuration.Persisted.Theme);
        Assert.NotEmpty(configuration.Persisted.WorkspacePath);
        Assert.NotEmpty(configuration.Persisted.LogDirectoryPath);
    }

    [Fact]
    public async Task ConcurrentLoadsShareDelayedInitializationAndRemainIdempotentAfterEditing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var configuration = new RecordingConfigurationService(CreateValidConfiguration());
        configuration.BlockNextLoad();
        using var settings = new SettingsViewModel(configuration);

        var firstLoad = settings.LoadAsync(cancellationToken);
        await configuration.LoadEntered.Task.WaitAsync(cancellationToken);
        var concurrentLoad = settings.LoadAsync(cancellationToken);
        configuration.ReleaseLoad();
        await Task.WhenAll(firstLoad, concurrentLoad);

        Assert.Equal(1, configuration.LoadCount);
        settings.Theme = AppThemePreference.Dark;
        await settings.LoadAsync(cancellationToken);
        Assert.Equal(AppThemePreference.Dark, settings.Theme);
        Assert.Equal(1, configuration.LoadCount);
        Assert.True(await settings.FlushPendingChangesAsync(cancellationToken));
        Assert.Equal(AppThemePreference.Dark, configuration.Persisted.Theme);
    }

    [Fact]
    public async Task RedundantLoadDuringDelayedSaveCannotHideNewerChangesFromShutdownFlush()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var configuration = new RecordingConfigurationService(CreateValidConfiguration());
        using var settings = new SettingsViewModel(configuration);
        await settings.LoadAsync(cancellationToken);
        settings.Theme = AppThemePreference.Dark;
        configuration.BlockNextSettingsSave();

        var firstFlush = settings.FlushPendingChangesAsync(cancellationToken);
        await configuration.SettingsSaveEntered.Task.WaitAsync(cancellationToken);
        try
        {
            await settings
                .LoadAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            settings.ForceAppAnimations = true;
        }
        finally
        {
            configuration.ReleaseSettingsSave();
        }

        Assert.True(await firstFlush);
        Assert.False(configuration.Persisted.ForceAppAnimations);
        Assert.True(await settings.FlushPendingChangesAsync(cancellationToken));
        Assert.Equal(1, configuration.LoadCount);
        Assert.Equal(2, configuration.SettingsSaveCount);
        Assert.Equal(AppThemePreference.Dark, configuration.Persisted.Theme);
        Assert.True(configuration.Persisted.ForceAppAnimations);
    }

    private static AppConfiguration CreateValidConfiguration() => new()
    {
        IsOnboardingComplete = true,
        WorkspacePath = Path.Combine(Path.GetTempPath(), "LocaleSmith.Tests", "workspace"),
        SandboxPath = Path.Combine(Path.GetTempPath(), "LocaleSmith.Tests", "sandbox"),
        Theme = AppThemePreference.Light
    };

    private static EncryptedJsonConfigurationStore<AppConfiguration> CreateStore(
        string path,
        ISecretStore secrets) =>
        new(
            path,
            "LocaleSmith.ApplicationSettings.v1",
            new CredentialManagerMasterKeyStore(secrets));

    private sealed class RecordingConfigurationService :
        LocaleSmith.Presentation.Abstractions.IAppConfigurationService
    {
        private readonly object _gate = new();
        private AppConfiguration _persisted;
        private TaskCompletionSource? _loadRelease;
        private TaskCompletionSource? _settingsSaveRelease;
        private bool _blockNextLoad;
        private bool _blockNextSettingsSave;

        public RecordingConfigurationService(AppConfiguration initial)
        {
            _persisted = initial;
        }

        public AppConfiguration Persisted
        {
            get
            {
                lock (_gate)
                {
                    return _persisted;
                }
            }
        }

        public int LoadCount { get; private set; }

        public int SettingsSaveCount { get; private set; }

        public TaskCompletionSource LoadEntered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SettingsSaveEntered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? release = null;
            lock (_gate)
            {
                LoadCount++;
                if (_blockNextLoad)
                {
                    _blockNextLoad = false;
                    LoadEntered.TrySetResult();
                    release = _loadRelease?.Task;
                }
            }

            if (release is not null)
            {
                await release.WaitAsync(cancellationToken);
            }

            lock (_gate)
            {
                return _persisted;
            }
        }

        public Task SaveAsync(
            AppConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _persisted = configuration;
            }

            return Task.CompletedTask;
        }

        public async Task SaveSettingsAsync(
            AppSettingsUpdate settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? release = null;
            lock (_gate)
            {
                SettingsSaveCount++;
                if (_blockNextSettingsSave)
                {
                    _blockNextSettingsSave = false;
                    SettingsSaveEntered.TrySetResult();
                    release = _settingsSaveRelease?.Task;
                }
            }

            if (release is not null)
            {
                await release.WaitAsync(cancellationToken);
            }

            lock (_gate)
            {
                _persisted = _persisted with
                {
                    Language = settings.Language,
                    Theme = settings.Theme,
                    ForceAppAnimations = settings.ForceAppAnimations,
                    WorkspacePath = settings.WorkspacePath,
                    SandboxPath = settings.SandboxPath,
                    LogDirectoryPath = settings.LogDirectoryPath ?? _persisted.LogDirectoryPath
                };
            }
        }

        public void BlockNextLoad()
        {
            lock (_gate)
            {
                _loadRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                LoadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _blockNextLoad = true;
            }
        }

        public void ReleaseLoad() =>
            (_loadRelease ?? throw new InvalidOperationException("Load was not blocked."))
                .TrySetResult();

        public void BlockNextSettingsSave()
        {
            lock (_gate)
            {
                _settingsSaveRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                SettingsSaveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _blockNextSettingsSave = true;
            }
        }

        public void ReleaseSettingsSave() =>
            (_settingsSaveRelease ?? throw new InvalidOperationException("Settings save was not blocked."))
                .TrySetResult();
    }

    private sealed class StubSandboxRootManager : ICliSandboxRootManager
    {
        private HashSet<string> _roots = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> SandboxRoots => _roots;

        public void ReplaceSandboxRoots(IEnumerable<string> sandboxRoots) =>
            _roots = sandboxRoots.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RejectingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Network access is not expected while restoring settings.");
    }
}
