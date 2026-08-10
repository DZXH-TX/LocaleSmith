using System.Diagnostics;
using LocaleSmith.App.Services;
using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Models;
using LocaleSmith.Application.Services;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.App.Tests;

public sealed class TranslationLogServiceTests
{
    [Fact]
    public async Task TranslationQueueRecordsLifecycleAndProgressWithoutExposingFullPaths()
    {
        using var artifacts = new TestArtifactDirectory();
        var configuration = new RecordingConfigurationService(Path.Combine(artifacts.Path, "logs"));
        using var logs = new TranslationLogService(configuration);
        await using var scheduler = new CompletedScheduler();
        var queue = new PipelineTranslationQueueService(scheduler, logs);
        var sourcePath = Path.Combine(artifacts.Path, "private", "lifecycle.jar");
        var outputPath = Path.Combine(artifacts.Path, "output", "translated.jar");

        var handle = await queue.EnqueueAsync(
            new TranslationQueueRequest(sourcePath, outputPath, "saved-source"),
            TestContext.Current.CancellationToken);
        var result = await handle.Completion.WaitAsync(TestContext.Current.CancellationToken);
        await logs.WaitForSessionCompletionAsync(handle.JobId)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(handle.JobId, result.JobId);
        var session = Assert.Single(
            await logs.GetSessionsAsync(TestContext.Current.CancellationToken));
        var debug = await logs.ReadAsync(
            session,
            TranslationLogViewMode.Debug,
            TestContext.Current.CancellationToken);
        var allLevels = await logs.ReadAsync(
            session,
            TranslationLogViewMode.AllLevels,
            TestContext.Current.CancellationToken);
        Assert.Contains("Translation job accepted", debug, StringComparison.Ordinal);
        Assert.Contains("Stage changed", debug, StringComparison.Ordinal);
        Assert.Contains("Translation completed", debug, StringComparison.Ordinal);
        Assert.Contains("fraction=1.0000", allLevels, StringComparison.Ordinal);
        Assert.DoesNotContain(artifacts.Path, debug, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SlowLogConfigurationCannotOrphanAnAcceptedQueueJob()
    {
        using var logs = new TranslationLogService(new BlockingConfigurationService());
        await using var scheduler = new CompletedScheduler();
        var queue = new PipelineTranslationQueueService(scheduler, logs);
        var stopwatch = Stopwatch.StartNew();

        var handle = await queue.EnqueueAsync(
            new TranslationQueueRequest("slow-log.jar", "slow-log-output.jar", "model"),
            TestContext.Current.CancellationToken);
        var result = await handle.Completion.WaitAsync(TestContext.Current.CancellationToken);

        stopwatch.Stop();
        Assert.Equal(handle.JobId, result.JobId);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public async Task DisposeDuringSessionStartupCannotLeaveAnActiveWriter()
    {
        using var artifacts = new TestArtifactDirectory();
        var configuration = new GatedConfigurationService(Path.Combine(artifacts.Path, "logs"));
        var service = new TranslationLogService(configuration);
        var start = service.TryStartSessionAsync(
            Guid.NewGuid(),
            "dispose-race.jar",
            "model",
            TestContext.Current.CancellationToken);
        await configuration.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        service.Dispose();
        configuration.ReleaseLoad.TrySetResult();
        var session = await start.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Null(session);
        if (Directory.Exists(configuration.LogDirectoryPath))
        {
            Directory.Delete(configuration.LogDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task PersistsSeparateDebugAndAllLevelLogsAndDiscoversThemAfterRestart()
    {
        using var artifacts = new TestArtifactDirectory();
        var logDirectory = Path.Combine(artifacts.Path, "translation-logs");
        var configuration = new RecordingConfigurationService(logDirectory);
        var sourcePath = Path.Combine(artifacts.Path, "private-parent", "example-mod.jar");
        var jobId = Guid.NewGuid();
        TranslationLogSessionInfo session;

        using (var writer = new TranslationLogService(configuration))
        {
            session = Assert.IsType<TranslationLogSessionInfo>(
                await writer.TryStartSessionAsync(
                    jobId,
                    sourcePath,
                    "saved-model-source",
                    TestContext.Current.CancellationToken));
            Assert.True(writer.TryWrite(jobId, TranslationLogLevel.Trace, "Progress", "fraction=0.2500"));
            Assert.True(writer.TryWrite(jobId, TranslationLogLevel.Debug, "Pipeline", "stage=Translating"));
            Assert.True(writer.TryWrite(
                jobId,
                TranslationLogLevel.Warning,
                "Redaction",
                "authorization=Bearer very-secret-token api_key=sk-supersecret1234"));
            await writer.CompleteSessionAndWaitAsync(
                jobId,
                TranslationLogLevel.Information,
                "Translation completed");
        }

        Assert.EndsWith(".debug.log", session.DebugLogPath, StringComparison.Ordinal);
        Assert.EndsWith(".all.log", session.AllLevelsLogPath, StringComparison.Ordinal);
        var debug = await File.ReadAllTextAsync(
            session.DebugLogPath,
            TestContext.Current.CancellationToken);
        var allLevels = await File.ReadAllTextAsync(
            session.AllLevelsLogPath,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("fraction=0.2500", debug, StringComparison.Ordinal);
        Assert.Contains("stage=Translating", debug, StringComparison.Ordinal);
        Assert.Contains("Translation completed", debug, StringComparison.Ordinal);
        Assert.Contains("fraction=0.2500", allLevels, StringComparison.Ordinal);
        Assert.Contains("example-mod.jar", allLevels, StringComparison.Ordinal);
        Assert.DoesNotContain(artifacts.Path, allLevels, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("very-secret-token", allLevels, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-supersecret1234", allLevels, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", allLevels, StringComparison.Ordinal);

        using var reader = new TranslationLogService(configuration);
        var restored = Assert.Single(
            await reader.GetSessionsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(jobId, restored.JobId);
        Assert.Contains("example-mod.jar", restored.DisplayName, StringComparison.Ordinal);
        Assert.Equal(
            debug,
            await reader.ReadAsync(
                restored,
                TranslationLogViewMode.Debug,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            allLevels,
            await reader.ReadAsync(
                restored,
                TranslationLogViewMode.AllLevels,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentWritesRemainWholeAndDurable()
    {
        using var artifacts = new TestArtifactDirectory();
        var configuration = new RecordingConfigurationService(Path.Combine(artifacts.Path, "logs"));
        var jobId = Guid.NewGuid();
        using var service = new TranslationLogService(configuration);
        var session = Assert.IsType<TranslationLogSessionInfo>(
            await service.TryStartSessionAsync(
                jobId,
                "concurrent.jar",
                "model",
                TestContext.Current.CancellationToken));

        Parallel.For(
            0,
            64,
            index => Assert.True(service.TryWrite(
                jobId,
                TranslationLogLevel.Debug,
                "Concurrent",
                $"entry={index:D2}")));
        await service.CompleteSessionAndWaitAsync(jobId, TranslationLogLevel.Information, "done");

        var lines = await File.ReadAllLinesAsync(
            session.DebugLogPath,
            TestContext.Current.CancellationToken);
        Assert.Equal(66, lines.Length);
        Assert.All(lines, static line => Assert.StartsWith("[", line, StringComparison.Ordinal));
        for (var index = 0; index < 64; index++)
        {
            Assert.Contains(lines, line => line.Contains($"entry={index:D2}", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task StartingNewSessionsRetainsOnlyTheConfiguredNumberOfOwnedLogPairs()
    {
        using var artifacts = new TestArtifactDirectory();
        var logDirectory = Path.Combine(artifacts.Path, "logs");
        using var service = new TranslationLogService(
            new RecordingConfigurationService(logDirectory),
            maximumRetainedSessions: 2);

        for (var index = 0; index < 3; index++)
        {
            var jobId = Guid.NewGuid();
            _ = Assert.IsType<TranslationLogSessionInfo>(await service.TryStartSessionAsync(
                jobId,
                $"retention-{index}.jar",
                "model",
                TestContext.Current.CancellationToken));
            await service.CompleteSessionAndWaitAsync(
                jobId,
                TranslationLogLevel.Information,
                "done");
            await Task.Delay(TimeSpan.FromMilliseconds(2), TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            2,
            Directory.EnumerateFiles(logDirectory, "*.debug.log", SearchOption.TopDirectoryOnly).Count());
        Assert.Equal(
            2,
            Directory.EnumerateFiles(logDirectory, "*.all.log", SearchOption.TopDirectoryOnly).Count());
        Assert.Equal(
            2,
            (await service.GetSessionsAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task RetentionDoesNotDeleteAForeignFileThatOnlyMatchesTheOwnedNamePattern()
    {
        using var artifacts = new TestArtifactDirectory();
        var logDirectory = Directory.CreateDirectory(Path.Combine(artifacts.Path, "logs")).FullName;
        var foreignJobId = Guid.NewGuid();
        var foreignPrefix = $"20000101T000000000Z_{foreignJobId:N}";
        var foreignDebugPath = Path.Combine(logDirectory, foreignPrefix + ".debug.log");
        var foreignAllPath = Path.Combine(logDirectory, foreignPrefix + ".all.log");
        await File.WriteAllTextAsync(
            foreignDebugPath,
            "foreign file with no LocaleSmith session header",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            foreignAllPath,
            "foreign file with no LocaleSmith session header",
            TestContext.Current.CancellationToken);
        using var service = new TranslationLogService(
            new RecordingConfigurationService(logDirectory),
            maximumRetainedSessions: 1);
        var jobId = Guid.NewGuid();

        _ = Assert.IsType<TranslationLogSessionInfo>(await service.TryStartSessionAsync(
            jobId,
            "owned.jar",
            "model",
            TestContext.Current.CancellationToken));
        await service.CompleteSessionAndWaitAsync(
            jobId,
            TranslationLogLevel.Information,
            "done");

        Assert.True(File.Exists(foreignDebugPath));
        Assert.True(File.Exists(foreignAllPath));
    }

    [Fact]
    public async Task InvalidConfiguredDirectoryDisablesLoggingWithoutThrowing()
    {
        var root = Path.GetPathRoot(AppContext.BaseDirectory)!;
        using var service = new TranslationLogService(new RecordingConfigurationService(root));

        var session = await service.TryStartSessionAsync(
            Guid.NewGuid(),
            "example.jar",
            "model",
            TestContext.Current.CancellationToken);

        Assert.Null(session);
    }

    [Fact]
    public async Task SessionDiscoveryBoundsMalformedFirstLineReads()
    {
        using var artifacts = new TestArtifactDirectory();
        var logDirectory = Path.Combine(artifacts.Path, "logs");
        Directory.CreateDirectory(logDirectory);
        var jobId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var path = Path.Combine(
            logDirectory,
            $"{startedAt:yyyyMMdd'T'HHmmssfff'Z'}_{jobId:N}.debug.log");
        await File.WriteAllTextAsync(
            path,
            new string('x', 1024 * 1024),
            TestContext.Current.CancellationToken);
        using var service = new TranslationLogService(
            new RecordingConfigurationService(logDirectory));

        var session = Assert.Single(
            await service.GetSessionsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(jobId, session.JobId);
        Assert.DoesNotContain(new string('x', 4096), session.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReaderRejectsAPathOutsideTheConfiguredDirectory()
    {
        using var artifacts = new TestArtifactDirectory();
        var logDirectory = Path.Combine(artifacts.Path, "logs");
        var outside = Path.Combine(artifacts.Path, "outside.debug.log");
        await File.WriteAllTextAsync(outside, "outside", TestContext.Current.CancellationToken);
        var jobId = Guid.NewGuid();
        var session = new TranslationLogSessionInfo(
            jobId,
            "outside",
            DateTimeOffset.UtcNow,
            outside,
            outside);
        using var service = new TranslationLogService(new RecordingConfigurationService(logDirectory));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAsync(
            session,
            TranslationLogViewMode.Debug,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReaderDoesNotFollowAReparsePointLogFile()
    {
        using var artifacts = new TestArtifactDirectory();
        var logDirectory = Path.Combine(artifacts.Path, "logs");
        Directory.CreateDirectory(logDirectory);
        var outside = Path.Combine(artifacts.Path, "outside.log");
        await File.WriteAllTextAsync(outside, "outside", TestContext.Current.CancellationToken);
        var jobId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var link = Path.Combine(
            logDirectory,
            $"{startedAt:yyyyMMdd'T'HHmmssfff'Z'}_{jobId:N}.debug.log");
        try
        {
            _ = File.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            PlatformNotSupportedException)
        {
            return;
        }

        using var service = new TranslationLogService(
            new RecordingConfigurationService(logDirectory));
        var session = new TranslationLogSessionInfo(
            jobId,
            "linked",
            startedAt,
            link,
            Path.ChangeExtension(link, ".all.log"));

        Assert.Empty(await service.GetSessionsAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAsync(
            session,
            TranslationLogViewMode.Debug,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LogsViewModelDefaultsToDebugAndCanSwitchToAllLevels()
    {
        using var artifacts = new TestArtifactDirectory();
        var configuration = new RecordingConfigurationService(Path.Combine(artifacts.Path, "logs"));
        using var service = new TranslationLogService(configuration);
        var jobId = Guid.NewGuid();
        _ = Assert.IsType<TranslationLogSessionInfo>(await service.TryStartSessionAsync(
            jobId,
            "view-model.jar",
            "model",
            TestContext.Current.CancellationToken));
        Assert.True(service.TryWrite(jobId, TranslationLogLevel.Trace, "Progress", "trace-only"));
        Assert.True(service.TryWrite(jobId, TranslationLogLevel.Debug, "Pipeline", "debug-entry"));
        await service.CompleteSessionAndWaitAsync(jobId, TranslationLogLevel.Information, "done");
        using var viewModel = new TranslationLogsViewModel(
            service,
            FallbackUiTextProvider.Instance,
            new InlineUiDispatcher());

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TranslationLogViewMode.Debug, viewModel.SelectedView);
        Assert.True(viewModel.HasSessions);
        Assert.True(viewModel.HasLogText);
        Assert.Contains("debug-entry", viewModel.LogText, StringComparison.Ordinal);
        Assert.DoesNotContain("trace-only", viewModel.LogText, StringComparison.Ordinal);

        viewModel.SelectedView = TranslationLogViewMode.AllLevels;
        await WaitUntilAsync(
            () => viewModel.LogText.Contains("trace-only", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        Assert.Contains("trace-only", viewModel.LogText, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private sealed class RecordingConfigurationService(string logDirectoryPath) : IAppConfigurationService
    {
        private AppConfiguration _configuration = new()
        {
            LogDirectoryPath = logDirectoryPath
        };

        public Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_configuration);
        }

        public Task SaveAsync(
            AppConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _configuration = configuration;
            return Task.CompletedTask;
        }

        public Task SaveSettingsAsync(
            AppSettingsUpdate settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _configuration = _configuration with
            {
                LogDirectoryPath = settings.LogDirectoryPath ?? _configuration.LogDirectoryPath
            };
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingConfigurationService : IAppConfigurationService
    {
        public async Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking configuration load should be canceled.");
        }

        public Task SaveAsync(
            AppConfiguration configuration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveSettingsAsync(
            AppSettingsUpdate settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class GatedConfigurationService(string logDirectoryPath) : IAppConfigurationService
    {
        public TaskCompletionSource LoadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseLoad { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string LogDirectoryPath { get; } = logDirectoryPath;

        public async Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadStarted.TrySetResult();
            await ReleaseLoad.Task.WaitAsync(cancellationToken);
            return new AppConfiguration { LogDirectoryPath = LogDirectoryPath };
        }

        public Task SaveAsync(
            AppConfiguration configuration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveSettingsAsync(
            AppSettingsUpdate settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CompletedScheduler : IPipelineJobScheduler
    {
        private PipelineProgress? _latestProgress;

        public event EventHandler<PipelineProgress>? ProgressChanged;

        public ValueTask<PipelineJobHandle> EnqueueAsync(
            PipelineRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var jobId = request.RequestedJobId ?? Guid.NewGuid();
            var artifact = new PackageArtifact(TranslationStyle.Formal, request.OutputPath);
            var inspection = new ArchiveInspection(
                "package-id",
                "example",
                "Fabric",
                UsedFileNameFallback: false,
                ArchiveSignatureState.None,
                CanResign: false,
                Warnings: []);
            var now = DateTimeOffset.UtcNow;
            _latestProgress = new PipelineProgress(
                jobId,
                PipelineStage.Completed,
                1,
                "complete",
                NextStage: null,
                Stages:
                [
                    new PipelineStageProgress(
                        PipelineStage.Committing,
                        PipelineStageStatus.Completed,
                        now,
                        now)
                ],
                RollbackStatus: PipelineStageStatus.Skipped);
            ProgressChanged?.Invoke(this, _latestProgress);
            var result = new PipelineResult(
                jobId,
                request.OutputPath,
                inspection,
                SourceEntryCount: 1,
                TranslatedEntryCount: 1,
                ReusedEntryCount: 0,
                HardcodedCandidates: [],
                new ExternalizationReport(0, 0, []),
                [artifact],
                new PackageVerification(true, true, [], [artifact]),
                Warnings: []);
            return ValueTask.FromResult(new PipelineJobHandle(
                jobId,
                Task.FromResult(result),
                static () => { },
                () => _latestProgress));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestArtifactDirectory : IDisposable
    {
        public TestArtifactDirectory()
        {
            var root = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory, ".test-artifacts"));
            Path = System.IO.Path.Combine(root, "translation-logs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
