using System.Net;
using LocaleSmith.Application;
using LocaleSmith.Application.Models;
using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.Services;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.Presentation.Tests;

public sealed class DashboardViewModelTests
{
    [Fact]
    public async Task EnqueuedPackageIsMirroredIntoSharedProjectWorkspace()
    {
        var source = CreateSource("one", "First");
        var queue = new ControllableQueueService();
        var workspace = new InMemoryModProjectWorkspace();
        var viewModel = new DashboardViewModel(
            new RecordingSelectionService(source),
            queue,
            new FixedOutputPathStrategy(),
            new InlineUiDispatcher(),
            projectWorkspace: workspace);
        string input = Path.GetTempFileName();
        try
        {
            await viewModel.EnqueuePackagesAsync([input], TestContext.Current.CancellationToken);

            PendingJob pending = Assert.Single(queue.Pending);
            ModProjectSnapshot project = Assert.IsType<ModProjectSnapshot>(workspace.ActiveProject);
            ModProjectTaskSnapshot task = Assert.IsType<ModProjectTaskSnapshot>(project.ActiveTask);
            Assert.Equal(pending.JobId, task.JobId);
            Assert.Equal(source.Id, task.ModelSourceId);
            Assert.Equal(Path.GetFullPath(input), task.SourcePath);

            pending.Completion.SetResult(new TranslationQueueResult(
                pending.JobId,
                pending.Request.OutputPath,
                "examplemod",
                "Fabric",
                [pending.Request.OutputPath],
                [],
                0));
            await WaitUntilAsync(
                () => workspace.ActiveProject?.LatestTask?.Status == ModProjectTaskStatus.Completed,
                TestContext.Current.CancellationToken);

            Assert.Equal("examplemod", workspace.ActiveProject?.ModId);
            Assert.Equal(ModProjectTaskStatus.Completed, workspace.ActiveProject?.LatestTask?.Status);
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public async Task SelectionIsImmediateAndQueueReportsCompletion()
    {
        var sourceOne = CreateSource("one", "First");
        var sourceTwo = CreateSource("two", "Second");
        var selection = new RecordingSelectionService(sourceOne, sourceTwo);
        var queue = new ControllableQueueService();
        var output = new FixedOutputPathStrategy();
        var viewModel = new DashboardViewModel(selection, queue, output, new InlineUiDispatcher());
        viewModel.SelectedModelSource = viewModel.ModelSources.Single(source => source.Id == "two");
        viewModel.SelectedTranslationStyle = viewModel.TranslationStyles.Single(
            option => option.Style == TranslationStyle.Informal);
        viewModel.SelectedTargetLanguage = viewModel.TargetLanguages.Single(
            option => option.CanonicalLocale == "ja_JP");

        await selection.SelectionObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("two", selection.SelectedSource?.Id);

        var input = Path.GetTempFileName();
        try
        {
            await viewModel.EnqueuePackagesAsync([input], TestContext.Current.CancellationToken);
            var pending = Assert.Single(queue.Pending);
            viewModel.SelectedTargetLanguage = viewModel.TargetLanguages.Single(
                option => option.CanonicalLocale == "fr_FR");
            Assert.Equal("two", pending.Request.ModelSourceId);
            Assert.Equal(TranslationStyle.Informal, pending.Request.Style);
            Assert.Equal("ja_JP", pending.Request.TargetLanguage);
            Assert.Equal("ja_JP", output.LastTargetLanguage);
            queue.Report(pending.JobId, PipelineStage.Translating, 0.5);
            pending.Completion.SetResult(new TranslationQueueResult(
                pending.JobId,
                output.OutputPath,
                "examplemod",
                "Fabric",
                ["informal.jar"],
                [],
                0,
                TranslationStyle.Informal,
                "ja_JP"));

            await WaitUntilAsync(
                () => Assert.Single(viewModel.QueueItems).Stage == PipelineStage.Completed,
                TestContext.Current.CancellationToken);
            var item = Assert.Single(viewModel.QueueItems);
            Assert.Equal("examplemod", item.ModId);
            Assert.Equal(100, item.ProgressPercent);
            Assert.Equal(TranslationStyle.Informal, item.Style);
            Assert.Equal("ja_JP", item.TargetLanguage);
            Assert.Contains("Japanese", item.TranslationProfile, StringComparison.Ordinal);
            Assert.True(item.ArtifactReady);
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public void LateLoadedAndEditedSourcesRefreshFriendlyOptionsAndPreserveSelection()
    {
        var selection = new RecordingSelectionService();
        var viewModel = new DashboardViewModel(
            selection,
            new ControllableQueueService(),
            new FixedOutputPathStrategy(),
            new InlineUiDispatcher());
        Assert.Empty(viewModel.ModelSources);
        Assert.False(viewModel.HasModelSources);
        Assert.False(viewModel.CanEnqueuePackages);
        Assert.Equal(TranslationStyle.Formal, viewModel.SelectedTranslationStyle.Style);
        Assert.Equal(
            ["zh_CN", "en_US", "ja_JP", "fr_FR", "ru_RU"],
            viewModel.TargetLanguages.Select(static language => language.CanonicalLocale));
        Assert.Equal(
            TranslationLanguageCatalog.DefaultLocale,
            viewModel.SelectedTargetLanguage.CanonicalLocale);

        var sourceOne = CreateSource("one", "Local translations", "llama3");
        var sourceTwo = CreateSource("two", "Team glossary", "qwen2.5:7b");
        selection.ReplaceSources([sourceOne, sourceTwo], selectedId: "two");

        Assert.Equal(["one", "two"], viewModel.ModelSources.Select(static source => source.Id));
        Assert.Equal("two", viewModel.SelectedModelSourceId);
        Assert.Equal("Team glossary", viewModel.SelectedModelSource?.DisplayName);
        Assert.Contains("qwen2.5:7b", viewModel.SelectedModelSource?.AccessibleLabel, StringComparison.Ordinal);
        Assert.True(viewModel.CanEnqueuePackages);

        var editedSourceTwo = CreateSource("two", "Team glossary updated", "qwen2.5:14b");
        selection.ReplaceSources([sourceOne, editedSourceTwo], selectedId: "two");

        Assert.Equal("two", viewModel.SelectedModelSourceId);
        Assert.Equal("Team glossary updated", viewModel.SelectedModelSource?.DisplayName);
        Assert.Equal("qwen2.5:14b", viewModel.SelectedModelSource?.ModelName);
    }

    [Fact]
    public async Task RemovedSelectionUsesAnnouncedAvailableFallback()
    {
        var sourceOne = CreateSource("one", "Local translations", "llama3");
        var sourceTwo = CreateSource("two", "Team glossary", "qwen2.5:7b");
        var selection = new RecordingSelectionService(sourceOne, sourceTwo);
        Assert.True(await selection.SelectSourceAsync("two", TestContext.Current.CancellationToken));
        var text = new DictionaryTextProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["QueueModelFallback"] = "Fallback to {0} with {1}."
        });
        var viewModel = new DashboardViewModel(
            selection,
            new ControllableQueueService(),
            new FixedOutputPathStrategy(),
            new InlineUiDispatcher(),
            text);

        selection.ReplaceSources([sourceOne], selectedId: "one");

        Assert.Equal("one", viewModel.SelectedModelSourceId);
        Assert.Equal("Fallback to Local translations with llama3.", viewModel.StatusMessage);

        selection.ReplaceSources([], selectedId: null);

        Assert.Null(viewModel.SelectedModelSource);
        Assert.False(viewModel.CanEnqueuePackages);
        Assert.Equal(viewModel.EmptyModelSourcesMessage, viewModel.StatusMessage);
    }

    [Fact]
    public async Task RejectedUnavailableSelectionRevertsAndIsNeverUsedByQueue()
    {
        var source = CreateSource("one", "Local translations", "llama3");
        var selection = new RecordingSelectionService(source);
        var queue = new ControllableQueueService();
        var viewModel = new DashboardViewModel(
            selection,
            queue,
            new FixedOutputPathStrategy(),
            new InlineUiDispatcher());
        var unavailable = CreateSource("gone", "Removed source", "removed-model");
        viewModel.ModelSources.Add(new ModelSourceOptionViewModel(unavailable));

        viewModel.SelectedModelSource = viewModel.ModelSources.Single(option => option.Id == "gone");
        await WaitUntilAsync(
            () => !viewModel.IsModelSelectionPending && viewModel.ErrorMessage is not null,
            TestContext.Current.CancellationToken);

        Assert.Equal("one", viewModel.SelectedModelSourceId);
        Assert.Contains("no longer available", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var input = Path.GetTempFileName();
        try
        {
            await viewModel.EnqueuePackagesAsync([input], TestContext.Current.CancellationToken);
            Assert.Equal("one", Assert.Single(queue.Pending).Request.ModelSourceId);
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public async Task CancelDelegatesToHandleWithoutInventingARollbackStage()
    {
        var source = CreateSource("one", "First");
        var queue = new ControllableQueueService();
        var viewModel = new DashboardViewModel(
            new RecordingSelectionService(source),
            queue,
            new FixedOutputPathStrategy(),
            new InlineUiDispatcher());
        var input = Path.GetTempFileName();
        try
        {
            await viewModel.EnqueuePackagesAsync([input], TestContext.Current.CancellationToken);
            var item = Assert.Single(viewModel.QueueItems);

            viewModel.CancelCommand.Execute(item);

            Assert.True(Assert.Single(queue.Pending).WasCancelled);
            Assert.True(item.IsCancellationRequested || item.Stage == PipelineStage.Cancelled);
            Assert.False(item.CanCancel);
            Assert.NotEqual(PipelineStage.RollingBack, item.Stage);
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public void ProgressAndFailureUseLocalizedPresentationText()
    {
        var text = new DictionaryTextProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["QueueProgressTranslating"] = "正在本地化翻译…",
            ["QueueStatusFailed"] = "处理失败",
            ["QueueFailureSummaryNoRollback"] = "任务安全失败，源文件未改变。"
        });
        var item = new QueueItemViewModel(
            Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), "source.jar"),
            Path.Combine(Path.GetTempPath(), "output.jar"),
            text);

        item.Update(new TranslationQueueProgress(item.JobId, PipelineStage.Translating, 0.5));

        Assert.Equal("正在本地化翻译…", item.Status);
        item.Fail("backend technical detail");
        Assert.Equal("任务安全失败，源文件未改变。", item.ErrorDetails);
        Assert.Equal("backend technical detail", item.TechnicalErrorDetails);
    }

    [Fact]
    public void CompletedQueueItemDisplaysProviderReportedModelUsage()
    {
        var item = new QueueItemViewModel(
            Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), "usage-test.jar"),
            Path.Combine(Path.GetTempPath(), "usage-test-output.jar"));
        var usage = ModelTokenUsage.FromProviderResponse(1_200, 300, 1_500)!;
        item.Complete(new TranslationQueueResult(
            item.JobId,
            item.OutputPath,
            "usage-test",
            "Fabric",
            [item.OutputPath],
            [],
            0,
            ModelUsage: usage));

        Assert.Same(usage, item.ModelUsage);
        Assert.True(item.HasModelUsage);
        Assert.Contains("1,500", item.ModelUsageSummary, StringComparison.Ordinal);
        Assert.Contains("Tokens", item.ModelUsageSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedQueueItemRetainsUsageReportedBeforeRollback()
    {
        var item = new QueueItemViewModel(
            Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), "failed-usage-test.jar"),
            Path.Combine(Path.GetTempPath(), "failed-usage-test-output.jar"));
        var usage = new ModelTokenUsage(
            inputTokens: 400,
            outputTokens: 50,
            totalTokens: 450,
            providerCallCount: 2,
            callsWithUsage: 1,
            callsWithCompleteUsage: 1);
        item.Update(new TranslationQueueProgress(
            item.JobId,
            PipelineStage.Failed,
            0.5,
            ModelUsage: usage));
        item.Fail("simulated failure");

        Assert.Same(usage, item.ModelUsage);
        Assert.True(item.HasModelUsage);
        Assert.Contains("1/2", item.ModelUsageSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void UnauthorizedModelFailureShowsActionableLocalizedGuidance()
    {
        var text = new DictionaryTextProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["QueueFailureSummaryNoRollback"] = "任务安全失败。",
            ["QueueFailureModelCredentials"] = "请更新 API Key 并测试连接。"
        });
        var item = new QueueItemViewModel(
            Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), "source.jar"),
            Path.Combine(Path.GetTempPath(), "output.jar"),
            text);
        var modelFailure = new ModelServiceException(
            "OpenAI-compatible endpoint returned HTTP 401 (Unauthorized).",
            HttpStatusCode.Unauthorized,
            responseBody: "provider body must not be displayed",
            requestId: "request-401");
        var pipelineFailure = new PipelineException(
            item.JobId,
            PipelineStage.Translating,
            "generic outer message",
            modelFailure);

        item.Fail(pipelineFailure);

        Assert.Equal("任务安全失败。 请更新 API Key 并测试连接。", item.ErrorDetails);
        Assert.Contains("stage=Translating", item.TechnicalErrorDetails, StringComparison.Ordinal);
        Assert.Contains("cause=ModelServiceException", item.TechnicalErrorDetails, StringComparison.Ordinal);
        Assert.Contains("http=401", item.TechnicalErrorDetails, StringComparison.Ordinal);
        Assert.Contains("request=request-401", item.TechnicalErrorDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("provider body", item.TechnicalErrorDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFailureDoesNotExposeItsArbitraryMessage()
    {
        var item = new QueueItemViewModel(
            Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), "source.jar"),
            Path.Combine(Path.GetTempPath(), "output.jar"));
        var pipelineFailure = new PipelineException(
            item.JobId,
            PipelineStage.Analyzing,
            "generic outer message",
            new InvalidOperationException("authorization=Bearer must-not-be-displayed"));

        item.Fail(pipelineFailure);

        Assert.Contains("stage=Analyzing", item.TechnicalErrorDetails, StringComparison.Ordinal);
        Assert.Contains("cause=InvalidOperationException", item.TechnicalErrorDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-be-displayed", item.TechnicalErrorDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelContractFailureDoesNotExposeModelControlledDetails()
    {
        var item = new QueueItemViewModel(
            Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), "source.jar"),
            Path.Combine(Path.GetTempPath(), "output.jar"));
        var pipelineFailure = new PipelineException(
            item.JobId,
            PipelineStage.Translating,
            "generic outer message",
            new TranslationContractException(
                "model-id\r\nauthorization=Bearer must-not-be-displayed"));

        item.Fail(pipelineFailure);

        Assert.Contains("stage=Translating", item.TechnicalErrorDetails, StringComparison.Ordinal);
        Assert.Contains("cause=TranslationContractException", item.TechnicalErrorDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("model-id", item.TechnicalErrorDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-be-displayed", item.TechnicalErrorDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectPipelineFailureDoesNotExposeArchiveControlledDetails()
    {
        var item = new QueueItemViewModel(
            Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), "source.jar"),
            Path.Combine(Path.GetTempPath(), "output.jar"));
        var pipelineFailure = new PipelineException(
            item.JobId,
            PipelineStage.Verifying,
            "archive-error\r\nauthorization=Bearer must-not-be-displayed");

        item.Fail(pipelineFailure);

        Assert.Contains("stage=Verifying", item.TechnicalErrorDetails, StringComparison.Ordinal);
        Assert.Contains("cause=PipelineException", item.TechnicalErrorDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("archive-error", item.TechnicalErrorDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-be-displayed", item.TechnicalErrorDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressDetailsUseStableStagesAndExposeRealRollbackState()
    {
        var text = new DictionaryTextProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["QueueProgressTranslating"] = "Localized current action",
            ["QueueProgressWriting"] = "Localized next action",
            ["QueueStageTranslating"] = "Localized translation stage",
            ["QueueStageStatusCurrent"] = "Localized current status",
            ["QueueStageRollingBack"] = "Localized rollback stage",
            ["QueueStageStatusCompleted"] = "Localized completed status",
            ["QueueRollbackStatus"] = "Localized rollback: {0}"
        });
        var item = new QueueItemViewModel(
            Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), "source.jar"),
            Path.Combine(Path.GetTempPath(), "output.jar"),
            text);
        var now = DateTimeOffset.UtcNow;

        item.Update(new TranslationQueueProgress(
            item.JobId,
            PipelineStage.Translating,
            0.4,
            PipelineStage.Writing,
            [
                new PipelineStageProgress(PipelineStage.Translating, PipelineStageStatus.Current, now, null)
            ],
            PipelineStageStatus.Pending));

        Assert.Equal("Localized current action", item.CurrentAction);
        Assert.Equal("Localized next action", item.NextAction);
        Assert.True(item.HasNextAction);
        Assert.False(item.HasRollbackStatus);
        var translating = Assert.Single(item.StageDetails);
        Assert.Equal("Localized translation stage", translating.StageName);
        Assert.Equal("Localized current status", translating.StatusText);
        Assert.True(translating.IsCurrent);
        Assert.True(translating.HasTiming);
        Assert.Contains(
            "Localized translation stage: Localized current status",
            item.StageDetailsSummary,
            StringComparison.Ordinal);

        item.Update(new TranslationQueueProgress(
            item.JobId,
            PipelineStage.Failed,
            0.4,
            NextStage: null,
            [
                new PipelineStageProgress(PipelineStage.Translating, PipelineStageStatus.Failed, now, now),
                new PipelineStageProgress(PipelineStage.RollingBack, PipelineStageStatus.Completed, now, now)
            ],
            PipelineStageStatus.Completed));

        Assert.False(item.HasNextAction);
        Assert.True(item.HasRollbackStatus);
        Assert.Equal("Localized rollback: Localized completed status", item.RollbackStatusText);
        Assert.Contains(item.StageDetails, stage =>
            stage.Stage == PipelineStage.RollingBack && stage.IsCompleted);
        Assert.Contains("Localized rollback stage", item.StageDetailsSummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PipelineStageStatus.Completed, "cancelled safely", "failed safely")]
    [InlineData(PipelineStageStatus.Failed, "rollback warning", "failure rollback warning")]
    [InlineData(PipelineStageStatus.Pending, "no rollback", "failure before commit")]
    public void TerminalCopyReflectsActualRollbackOutcome(
        PipelineStageStatus rollbackStatus,
        string expectedCancellation,
        string expectedFailure)
    {
        var text = new DictionaryTextProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["QueueStatusCancelledRolledBack"] = "cancelled safely",
            ["QueueStatusCancelledRollbackFailed"] = "rollback warning",
            ["QueueStatusCancelledNoRollback"] = "no rollback",
            ["QueueFailureSummaryRolledBack"] = "failed safely",
            ["QueueFailureSummaryRollbackFailed"] = "failure rollback warning",
            ["QueueFailureSummaryNoRollback"] = "failure before commit"
        });
        var cancelled = new QueueItemViewModel(
            Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), "source.jar"),
            Path.Combine(Path.GetTempPath(), "output.jar"),
            text);
        cancelled.Update(new TranslationQueueProgress(
            cancelled.JobId,
            PipelineStage.Cancelled,
            0,
            RollbackStatus: rollbackStatus));
        cancelled.Cancelled();

        var failed = new QueueItemViewModel(
            Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), "source.jar"),
            Path.Combine(Path.GetTempPath(), "output.jar"),
            text);
        failed.Update(new TranslationQueueProgress(
            failed.JobId,
            PipelineStage.Failed,
            0,
            RollbackStatus: rollbackStatus));
        failed.Fail("technical");

        Assert.Equal(expectedCancellation, cancelled.Status);
        Assert.Equal(expectedFailure, failed.ErrorDetails);
        Assert.Equal(rollbackStatus == PipelineStageStatus.Failed, cancelled.HasFailureDetails);
        Assert.True(failed.HasFailureDetails);
    }

    [Fact]
    public async Task SynchronousCompletionReplaysProgressAfterDashboardRegistersTheJob()
    {
        var source = CreateSource("one", "Saved model source", "saved-model");
        var queue = new ImmediatelyCompletingQueueService();
        var viewModel = new DashboardViewModel(
            new RecordingSelectionService(source),
            queue,
            new FixedOutputPathStrategy(),
            new InlineUiDispatcher());
        var input = Path.GetTempFileName();
        try
        {
            await viewModel.EnqueuePackagesAsync([input], TestContext.Current.CancellationToken);
            await WaitUntilAsync(
                () => Assert.Single(viewModel.QueueItems).Stage == PipelineStage.Completed,
                TestContext.Current.CancellationToken);

            var item = Assert.Single(viewModel.QueueItems);
            Assert.True(item.HasStageDetails);
            Assert.Equal(9, item.StageDetails.Count);
            Assert.All(item.StageDetails, stage => Assert.True(stage.IsCompleted));
            Assert.False(item.HasNextAction);
        }
        finally
        {
            File.Delete(input);
        }
    }

    private static ModelSource CreateSource(string id, string name, string modelName = "llama3") => new(
        id,
        name,
        ModelProviderKind.Ollama,
        new Uri("http://127.0.0.1:11434"),
        modelName);

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var timeout = TimeProvider.System.GetTimestamp() + TimeSpan.FromSeconds(2).Ticks;
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TimeProvider.System.GetTimestamp() > timeout)
            {
                throw new TimeoutException("The expected view-model state was not reached.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed class RecordingSelectionService : IModelSelectionService, IModelSelectionStateNotifier
    {
        private readonly List<ModelSource> _sources;
        private ModelSource? _selected;

        public RecordingSelectionService(params ModelSource[] sources)
        {
            _sources = [.. sources];
            _selected = _sources.FirstOrDefault();
        }

        public TaskCompletionSource SelectionObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ModelSource> Sources => _sources.ToArray();

        public ModelSource? SelectedSource => _selected;

        public event EventHandler<ModelSelectionStateChangedEventArgs>? StateChanged;

        public Task<bool> SelectSourceAsync(string sourceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = _sources.FirstOrDefault(source => source.Id == sourceId);
            if (source is null)
            {
                return Task.FromResult(false);
            }

            _selected = source;
            SelectionObserved.TrySetResult();
            StateChanged?.Invoke(this, new ModelSelectionStateChangedEventArgs(Sources, _selected));
            return Task.FromResult(true);
        }

        public void ReplaceSources(IEnumerable<ModelSource> sources, string? selectedId)
        {
            var previousId = _selected?.Id;
            _sources.Clear();
            _sources.AddRange(sources);
            _selected = selectedId is null
                ? previousId is null
                    ? null
                    : _sources.FirstOrDefault(source => source.Id == previousId)
                : _sources.FirstOrDefault(source => source.Id == selectedId);
            StateChanged?.Invoke(this, new ModelSelectionStateChangedEventArgs(Sources, _selected));
        }
    }

    private sealed class ControllableQueueService : ITranslationQueueService
    {
        public event EventHandler<TranslationQueueProgress>? ProgressChanged;

        public List<PendingJob> Pending { get; } = [];

        public ValueTask<TranslationQueueHandle> EnqueueAsync(
            TranslationQueueRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new PendingJob(Guid.NewGuid(), request);
            Pending.Add(pending);
            return ValueTask.FromResult(new TranslationQueueHandle(
                pending.JobId,
                pending.Completion.Task,
                pending.Cancel));
        }

        public void Report(Guid jobId, PipelineStage stage, double fraction) =>
            ProgressChanged?.Invoke(this, new TranslationQueueProgress(jobId, stage, fraction));
    }

    private sealed class ImmediatelyCompletingQueueService : ITranslationQueueService
    {
        public event EventHandler<TranslationQueueProgress>? ProgressChanged
        {
            add { }
            remove { }
        }

        public ValueTask<TranslationQueueHandle> EnqueueAsync(
            TranslationQueueRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var jobId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var stages = new[]
            {
                PipelineStage.Queued,
                PipelineStage.Inspecting,
                PipelineStage.Extracting,
                PipelineStage.Analyzing,
                PipelineStage.Translating,
                PipelineStage.Writing,
                PipelineStage.Repacking,
                PipelineStage.Verifying,
                PipelineStage.Committing
            }.Select(stage => new PipelineStageProgress(
                stage,
                PipelineStageStatus.Completed,
                now,
                now)).ToArray();
            var progress = new TranslationQueueProgress(
                jobId,
                PipelineStage.Completed,
                1,
                NextStage: null,
                stages,
                PipelineStageStatus.Skipped);
            var result = new TranslationQueueResult(
                jobId,
                request.OutputPath,
                "examplemod",
                "Fabric",
                [request.OutputPath],
                [],
                0,
                request.Style);
            return ValueTask.FromResult(new TranslationQueueHandle(
                jobId,
                Task.FromResult(result),
                static () => { },
                () => progress));
        }
    }

    private sealed class PendingJob(Guid jobId, TranslationQueueRequest request)
    {
        public Guid JobId { get; } = jobId;

        public TranslationQueueRequest Request { get; } = request;

        public TaskCompletionSource<TranslationQueueResult> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCancelled { get; private set; }

        public void Cancel()
        {
            WasCancelled = true;
            Completion.TrySetCanceled();
        }
    }

    private sealed class FixedOutputPathStrategy : IOutputPathStrategy
    {
        public string OutputPath { get; } = Path.Combine(Path.GetTempPath(), "translated.jar");

        public string? LastTargetLanguage { get; private set; }

        public Task<string> CreateOutputPathAsync(
            string sourcePath,
            string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastTargetLanguage = targetLanguage;
            return Task.FromResult(OutputPath);
        }
    }

    private sealed class DictionaryTextProvider(IReadOnlyDictionary<string, string> values) : IUiTextProvider
    {
        public string GetText(string key, string fallback, params object?[] arguments)
        {
            var template = values.TryGetValue(key, out var value) ? value : fallback;
            return arguments.Length == 0
                ? template
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, template, arguments);
        }
    }
}
