using LocaleSmith.Core.Services;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.Services;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.Presentation.Tests;

public sealed class OutputPathAndSettingsTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void AnimationPolicyOnlyOverridesTheDisabledSystemPreferenceWhenExplicitlyForced(
        bool systemAnimationsEnabled,
        bool forceAppAnimations,
        bool expected)
    {
        Assert.Equal(
            expected,
            AnimationPreferencePolicy.ShouldRunAppAnimations(
                systemAnimationsEnabled,
                forceAppAnimations));
    }

    [Fact]
    public void AppMotionDurationsStayWithinTheResponsiveInteractionRange()
    {
        Assert.InRange(AnimationPreferencePolicy.ButtonFeedbackDurationMilliseconds, 150, 300);
        Assert.InRange(AnimationPreferencePolicy.RevealDurationMilliseconds, 150, 300);
        Assert.InRange(AnimationPreferencePolicy.PageTransitionDurationMilliseconds, 150, 300);
    }

    [Fact]
    public async Task OutputStrategyReadsLatestWorkspaceForEveryRequest()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var sourcePath = Path.Combine(testRoot, "source", "example.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllTextAsync(sourcePath, "archive", TestContext.Current.CancellationToken);
            var firstWorkspace = Path.Combine(testRoot, "workspace-one");
            var secondWorkspace = Path.Combine(testRoot, "workspace-two");
            var configuration = new MutableConfigurationService(CreateConfiguration(firstWorkspace));
            var strategy = new DefaultOutputPathStrategy(configuration);

            var firstOutput = await strategy.CreateOutputPathAsync(
                sourcePath,
                TranslationLanguageCatalog.DefaultLocale,
                TestContext.Current.CancellationToken);
            configuration.Configuration = configuration.Configuration with
            {
                WorkspacePath = secondWorkspace
            };
            var secondOutput = await strategy.CreateOutputPathAsync(
                sourcePath,
                TranslationLanguageCatalog.DefaultLocale,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                Path.Combine(firstWorkspace, "LocaleSmith.Output", "example.zh_CN.jar"),
                firstOutput);
            Assert.Equal(
                Path.Combine(secondWorkspace, "LocaleSmith.Output", "example.zh_CN.jar"),
                secondOutput);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("zh_CN", "zh_CN")]
    [InlineData("en_US", "en_US")]
    [InlineData("ja-jp", "ja_JP")]
    [InlineData("fr_FR", "fr_FR")]
    [InlineData("ru_RU", "ru_RU")]
    public async Task OutputStrategyUsesTheCanonicalTargetLocaleInTheArtifactName(
        string targetLanguage,
        string expectedLocale)
    {
        var testRoot = CreateTestRoot();
        try
        {
            var sourcePath = Path.Combine(testRoot, "example.jar");
            await File.WriteAllTextAsync(sourcePath, "archive", TestContext.Current.CancellationToken);
            var workspace = Path.Combine(testRoot, "workspace");
            var strategy = new DefaultOutputPathStrategy(
                new MutableConfigurationService(CreateConfiguration(workspace)));

            string output = await strategy.CreateOutputPathAsync(
                sourcePath,
                targetLanguage,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                Path.Combine(workspace, "LocaleSmith.Output", $"example.{expectedLocale}.jar"),
                output);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OutputStrategyRejectsOutputNestedInsideDirectorySourceWithoutCreatingIt()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var sourceDirectory = Path.Combine(testRoot, "expanded-pack");
            Directory.CreateDirectory(sourceDirectory);
            var nestedWorkspace = Path.Combine(sourceDirectory, "workspace");
            var strategy = new DefaultOutputPathStrategy(
                new MutableConfigurationService(CreateConfiguration(nestedWorkspace)));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                strategy.CreateOutputPathAsync(
                    sourceDirectory,
                    TranslationLanguageCatalog.DefaultLocale,
                    TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(nestedWorkspace));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OutputStrategyRejectsReparseWorkspace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = CreateTestRoot();
        var workspaceLink = Path.Combine(testRoot, "workspace-link");
        try
        {
            var sourcePath = Path.Combine(testRoot, "example.jar");
            await File.WriteAllTextAsync(sourcePath, "archive", TestContext.Current.CancellationToken);
            var workspaceTarget = Path.Combine(testRoot, "workspace-target");
            Directory.CreateDirectory(workspaceTarget);
            try
            {
                Directory.CreateSymbolicLink(workspaceLink, workspaceTarget);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            var strategy = new DefaultOutputPathStrategy(
                new MutableConfigurationService(CreateConfiguration(workspaceLink)));

            await Assert.ThrowsAsync<IOException>(() =>
                strategy.CreateOutputPathAsync(
                    sourcePath,
                    TranslationLanguageCatalog.DefaultLocale,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(workspaceLink))
            {
                Directory.Delete(workspaceLink);
            }

            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OutputStrategyRejectsExistingReparseOutputFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = CreateTestRoot();
        var outputLink = Path.Combine(
            testRoot,
            "workspace",
            "LocaleSmith.Output",
            "example.zh_CN.jar");
        try
        {
            var sourcePath = Path.Combine(testRoot, "example.jar");
            var targetPath = Path.Combine(testRoot, "unrelated-target.jar");
            await File.WriteAllTextAsync(sourcePath, "archive", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(targetPath, "do-not-touch", TestContext.Current.CancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(outputLink)!);
            try
            {
                File.CreateSymbolicLink(outputLink, targetPath);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            var strategy = new DefaultOutputPathStrategy(
                new MutableConfigurationService(CreateConfiguration(Path.Combine(testRoot, "workspace"))));

            await Assert.ThrowsAsync<IOException>(() =>
                strategy.CreateOutputPathAsync(
                    sourcePath,
                    TranslationLanguageCatalog.DefaultLocale,
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                "do-not-touch",
                await File.ReadAllTextAsync(targetPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (File.Exists(outputLink))
            {
                File.Delete(outputLink);
            }

            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsSavePersistsNormalizedWorkspacePath()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var configuration = new MutableConfigurationService(
                CreateConfiguration(Path.Combine(testRoot, "workspace-old")));
            var viewModel = new SettingsViewModel(configuration);
            await viewModel.LoadAsync(TestContext.Current.CancellationToken);
            var updatedWorkspace = Path.Combine(testRoot, "workspace-new", "..");
            viewModel.WorkspacePath = updatedWorkspace;

            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.NotNull(configuration.SavedConfiguration);
            Assert.Equal(
                Path.GetFullPath(updatedWorkspace),
                configuration.SavedConfiguration.WorkspacePath);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsLoadAndSaveRoundTripsForcedAppAnimationPreference()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var configuration = new MutableConfigurationService(
                CreateConfiguration(Path.Combine(testRoot, "workspace")) with
                {
                    ForceAppAnimations = true
                });
            var viewModel = new SettingsViewModel(configuration);

            await viewModel.LoadAsync(TestContext.Current.CancellationToken);

            Assert.True(viewModel.ForceAppAnimations);
            viewModel.ForceAppAnimations = false;
            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.NotNull(configuration.SavedConfiguration);
            Assert.False(configuration.SavedConfiguration.ForceAppAnimations);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ChangingDisplayLanguageRequiresRestartUntilReverted()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var configuration = new MutableConfigurationService(
                CreateConfiguration(Path.Combine(testRoot, "workspace")) with
                {
                    Language = AppDisplayLanguages.DefaultLanguage
                });
            using var viewModel = new SettingsViewModel(configuration);

            await viewModel.LoadAsync(TestContext.Current.CancellationToken);
            Assert.False(viewModel.IsLanguageRestartRequired);

            viewModel.Language = AppDisplayLanguages.EnglishUnitedStates;
            Assert.True(viewModel.IsLanguageRestartRequired);
            Assert.False(viewModel.TryGetPersistedDisplayLanguageForRestart(out _));

            await viewModel.SaveCommand.ExecuteAsync(null);
            Assert.True(viewModel.TryGetPersistedDisplayLanguageForRestart(out var persistedLanguage));
            Assert.Equal(AppDisplayLanguages.EnglishUnitedStates, persistedLanguage);

            viewModel.Language = AppDisplayLanguages.DefaultLanguage;
            Assert.False(viewModel.IsLanguageRestartRequired);
            Assert.False(viewModel.TryGetPersistedDisplayLanguageForRestart(out _));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsLoadAndSaveRoundTripsNormalizedLogDirectoryPath()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var originalLogDirectory = Path.Combine(testRoot, "logs-old");
            var configuration = new MutableConfigurationService(
                CreateConfiguration(Path.Combine(testRoot, "workspace")) with
                {
                    LogDirectoryPath = originalLogDirectory
                });
            using var viewModel = new SettingsViewModel(configuration);
            await viewModel.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(originalLogDirectory, viewModel.LogDirectoryPath);
            var updatedLogDirectory = Path.Combine(testRoot, "logs-new", "..");
            viewModel.LogDirectoryPath = updatedLogDirectory;

            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.NotNull(configuration.SavedConfiguration);
            Assert.Equal(
                Path.GetFullPath(updatedLogDirectory),
                configuration.SavedConfiguration.LogDirectoryPath);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void LogDirectoryNormalizationRejectsFilesystemRoots()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory));
        Assert.False(string.IsNullOrWhiteSpace(root));

        Assert.Throws<ArgumentException>(() =>
            AppConfiguration.NormalizeLogDirectoryPath(root!));
    }

    [Fact]
    public void LogDirectoryNormalizationRejectsWindowsNetworkShares()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<ArgumentException>(() =>
            AppConfiguration.NormalizeLogDirectoryPath(@"\\server\share\LocaleSmithLogs"));
    }

    private static AppConfiguration CreateConfiguration(string workspacePath) => new()
    {
        IsOnboardingComplete = true,
        WorkspacePath = workspacePath,
        SandboxPath = Path.Combine(Path.GetTempPath(), "LocaleSmith", "Sandbox")
    };

    private static string CreateTestRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "LocaleSmith.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MutableConfigurationService(AppConfiguration configuration) : IAppConfigurationService
    {
        public AppConfiguration Configuration { get; set; } = configuration;

        public AppConfiguration? SavedConfiguration { get; private set; }

        public Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Configuration);
        }

        public Task SaveAsync(
            AppConfiguration configurationToSave,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SavedConfiguration = configurationToSave;
            Configuration = configurationToSave;
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
