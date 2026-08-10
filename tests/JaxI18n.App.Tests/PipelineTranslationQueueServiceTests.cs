using JaxI18n.App.Services;
using JaxI18n.Application.Abstractions;
using JaxI18n.Application.Models;
using JaxI18n.Application.Services;
using JaxI18n.Core.Models;
using JaxI18n.Presentation.Models;

namespace JaxI18n.App.Tests;

public sealed class PipelineTranslationQueueServiceTests
{
    [Theory]
    [InlineData(TranslationStyle.Formal)]
    [InlineData(TranslationStyle.Informal)]
    public async Task QueuePassesExactlyOneCapturedStyleAndReturnsOneArtifact(TranslationStyle style)
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "JaxI18n",
            "queue-style-tests",
            Guid.NewGuid().ToString("N"),
            "translated.jar");
        await using var scheduler = new RecordingScheduler();
        var service = new PipelineTranslationQueueService(scheduler);

        var handle = await service.EnqueueAsync(
            new TranslationQueueRequest("input.jar", outputPath, "saved-source", style),
            TestContext.Current.CancellationToken);
        var result = await handle.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, scheduler.EnqueueCount);
        Assert.NotNull(scheduler.Request);
        Assert.Equal(style, Assert.Single(scheduler.Request.Styles));
        Assert.Equal("saved-source", scheduler.Request.ModelSourceId);
        Assert.Equal(style, result.Style);
        Assert.Equal(outputPath, Assert.Single(result.ArtifactPaths));
        var latest = Assert.IsType<TranslationQueueProgress>(handle.LatestProgress);
        Assert.Equal(PipelineStage.Completed, latest.Stage);
        Assert.Equal(9, Assert.IsAssignableFrom<IReadOnlyList<PipelineStageProgress>>(latest.Stages).Count);
    }

    private sealed class RecordingScheduler : IPipelineJobScheduler
    {
        private PipelineProgress? _latestProgress;

        public event EventHandler<PipelineProgress>? ProgressChanged;

        public PipelineRequest? Request { get; private set; }

        public int EnqueueCount { get; private set; }

        public ValueTask<PipelineJobHandle> EnqueueAsync(
            PipelineRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            EnqueueCount++;
            var jobId = Guid.NewGuid();
            var artifact = new PackageArtifact(Assert.Single(request.Styles), request.OutputPath);
            var artifacts = new[] { artifact };
            var result = new PipelineResult(
                jobId,
                request.OutputPath,
                new ArchiveInspection(
                    "package-id",
                    "example",
                    "Fabric",
                    UsedFileNameFallback: false,
                    ArchiveSignatureState.None,
                    CanResign: false,
                    Warnings: []),
                SourceEntryCount: 1,
                TranslatedEntryCount: 1,
                ReusedEntryCount: 0,
                HardcodedCandidates: [],
                new ExternalizationReport(0, 0, []),
                artifacts,
                new PackageVerification(true, true, [], artifacts),
                Warnings: []);
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
            _latestProgress = new PipelineProgress(
                jobId,
                PipelineStage.Completed,
                1,
                "complete",
                NextStage: null,
                stages,
                PipelineStageStatus.Skipped);
            // Deliberately publish before EnqueueAsync returns. A dashboard cannot know the job id yet.
            ProgressChanged?.Invoke(this, _latestProgress);
            return ValueTask.FromResult(new PipelineJobHandle(
                jobId,
                Task.FromResult(result),
                static () => { },
                () => _latestProgress));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
