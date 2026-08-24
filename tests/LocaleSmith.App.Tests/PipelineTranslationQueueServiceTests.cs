using System.Net;
using LocaleSmith.App.Services;
using LocaleSmith.Application;
using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Models;
using LocaleSmith.Application.Services;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Tests;

public sealed class PipelineTranslationQueueServiceTests
{
    [Fact]
    public void FailureLogMessageIncludesPipelineStageAndSafeProviderDiagnostics()
    {
        var modelFailure = new ModelServiceException(
            "OpenAI-compatible endpoint returned HTTP 401 (Unauthorized).",
            HttpStatusCode.Unauthorized,
            requestId: "request-401");
        var pipelineFailure = new PipelineException(
            Guid.NewGuid(),
            PipelineStage.Translating,
            "The pipeline failed.",
            modelFailure);

        var message = PipelineTranslationQueueService.CreateFailureLogMessage(
            "Translation failed",
            pipelineFailure);

        Assert.Contains("stage=Translating", message, StringComparison.Ordinal);
        Assert.Contains("cause=ModelServiceException", message, StringComparison.Ordinal);
        Assert.Contains("http=401", message, StringComparison.Ordinal);
        Assert.Contains("request=request-401", message, StringComparison.Ordinal);
        Assert.Contains("detail=OpenAI-compatible endpoint returned HTTP 401", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TranslationStyle.Formal)]
    [InlineData(TranslationStyle.Informal)]
    public async Task QueuePassesExactlyOneCapturedStyleAndReturnsOneArtifact(TranslationStyle style)
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "LocaleSmith",
            "queue-style-tests",
            Guid.NewGuid().ToString("N"),
            "translated.jar");
        await using var scheduler = new RecordingScheduler();
        var service = new PipelineTranslationQueueService(scheduler);
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Assert.False(Directory.Exists(outputDirectory));

        var handle = await service.EnqueueAsync(
            new TranslationQueueRequest("input.jar", outputPath, "saved-source", style),
            TestContext.Current.CancellationToken);
        var result = await handle.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(outputDirectory));
        Assert.Equal(1, scheduler.EnqueueCount);
        Assert.NotNull(scheduler.Request);
        Assert.Equal(style, Assert.Single(scheduler.Request.Styles));
        Assert.Equal("saved-source", scheduler.Request.ModelSourceId);
        Assert.Equal("zh_CN", scheduler.Request.TargetLanguage);
        Assert.Equal(SignedArchiveHandling.CreateUnsignedCopy, scheduler.Request.SignedArchiveHandling);
        Assert.Equal(
            HardcodedStringMode.ExternalizeRecognizedSafePatterns,
            scheduler.Request.HardcodedStringMode);
        Assert.Equal(style, result.Style);
        Assert.Equal("zh_CN", result.TargetLanguage);
        Assert.Same(scheduler.ModelUsage, result.ModelUsage);
        Assert.Equal(outputPath, Assert.Single(result.ArtifactPaths));
        var latest = Assert.IsType<TranslationQueueProgress>(handle.LatestProgress);
        Assert.Equal(PipelineStage.Completed, latest.Stage);
        Assert.Equal(9, Assert.IsAssignableFrom<IReadOnlyList<PipelineStageProgress>>(latest.Stages).Count);
    }

    [Theory]
    [InlineData("zh_CN", "zh_CN")]
    [InlineData("en_US", "en_US")]
    [InlineData("ja-jp", "ja_JP")]
    [InlineData("fr_FR", "fr_FR")]
    [InlineData("ru_RU", "ru_RU")]
    public async Task QueuePassesTheCapturedTargetLanguage(
        string targetLanguage,
        string expectedCanonicalLocale)
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "LocaleSmith",
            "queue-language-tests",
            Guid.NewGuid().ToString("N"),
            "translated.jar");
        await using var scheduler = new RecordingScheduler();
        var service = new PipelineTranslationQueueService(scheduler);

        var handle = await service.EnqueueAsync(
            new TranslationQueueRequest(
                "input.jar",
                outputPath,
                "saved-source",
                TranslationStyle.Formal,
                targetLanguage),
            TestContext.Current.CancellationToken);
        TranslationQueueResult result = await handle.Completion.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.NotNull(scheduler.Request);
        Assert.Equal(expectedCanonicalLocale, scheduler.Request.TargetLanguage);
        Assert.Equal(expectedCanonicalLocale, result.TargetLanguage);
    }

    private sealed class RecordingScheduler : IPipelineJobScheduler
    {
        private PipelineProgress? _latestProgress;

        public event EventHandler<PipelineProgress>? ProgressChanged;

        public PipelineRequest? Request { get; private set; }

        public int EnqueueCount { get; private set; }

        public ModelTokenUsage ModelUsage { get; } = new(10, 4, 14, 1, 1, 1);

        public ValueTask<PipelineJobHandle> EnqueueAsync(
            PipelineRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            EnqueueCount++;
            var jobId = request.RequestedJobId ?? Guid.NewGuid();
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
                Warnings: [],
                ModelUsage: ModelUsage);
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
