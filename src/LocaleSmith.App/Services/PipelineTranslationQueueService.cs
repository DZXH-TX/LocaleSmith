using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Models;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Services;

public sealed class PipelineTranslationQueueService : ITranslationQueueService
{
    private readonly IPipelineJobScheduler _scheduler;

    public PipelineTranslationQueueService(IPipelineJobScheduler scheduler)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _scheduler.ProgressChanged += OnPipelineProgressChanged;
    }

    public event EventHandler<TranslationQueueProgress>? ProgressChanged;

    public async ValueTask<TranslationQueueHandle> EnqueueAsync(
        TranslationQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))
            ?? throw new InvalidOperationException("The output path must have a parent directory.");
        Directory.CreateDirectory(outputDirectory);

        var pipelineHandle = await _scheduler.EnqueueAsync(
            new PipelineRequest(
                request.SourcePath,
                request.OutputPath,
                targetLanguage: "zh_CN",
                styles: new HashSet<TranslationStyle> { request.Style },
                // Signed archives remain blocked until a dedicated, explicit signature-choice dialog exists.
                signedArchiveHandling: SignedArchiveHandling.Block,
                hardcodedStringMode: HardcodedStringMode.ExternalizeRecognizedSafePatterns,
                modelSourceId: request.ModelSourceId),
            cancellationToken).ConfigureAwait(false);

        return new TranslationQueueHandle(
            pipelineHandle.JobId,
            ConvertResultAsync(pipelineHandle.Completion, request.Style),
            pipelineHandle.Cancel,
            () => TranslateProgress(pipelineHandle.LatestProgress));
    }

    private static async Task<TranslationQueueResult> ConvertResultAsync(
        Task<PipelineResult> completion,
        TranslationStyle requestedStyle)
    {
        var result = await completion.ConfigureAwait(false);
        if (result.Artifacts.Count != 1 || result.Artifacts[0].Style != requestedStyle)
        {
            throw new InvalidDataException(
                "A processing-queue job must produce exactly one artifact in its captured translation style.");
        }

        return new TranslationQueueResult(
            result.JobId,
            result.OutputPath,
            result.Inspection.ModId,
            result.Inspection.Loader,
            result.Artifacts.Select(static artifact => artifact.Path).ToArray(),
            result.HardcodedCandidates.ToArray(),
            result.Externalization.ExternalizedCount,
            requestedStyle);
    }

    private void OnPipelineProgressChanged(object? sender, PipelineProgress progress)
    {
        var handlers = ProgressChanged;
        if (handlers is null)
        {
            return;
        }

        var translated = TranslateProgress(progress);
        if (translated is null)
        {
            return;
        }

        foreach (EventHandler<TranslationQueueProgress> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, translated);
            }
            catch
            {
                // A view subscriber must not terminate the transactional worker.
            }
        }
    }

    private static TranslationQueueProgress? TranslateProgress(PipelineProgress? progress)
    {
        if (progress is null)
        {
            return null;
        }

        return new TranslationQueueProgress(
            progress.JobId,
            progress.Stage,
            progress.Fraction,
            progress.NextStage,
            progress.Stages?.ToArray(),
            progress.RollbackStatus);
    }
}
