using System.Collections.Concurrent;
using System.Globalization;
using LocaleSmith.Application;
using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Models;
using LocaleSmith.Application.Services;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Services;

public sealed class PipelineTranslationQueueService : ITranslationQueueService
{
    private readonly IPipelineJobScheduler _scheduler;
    private readonly TranslationLogService? _translationLogs;
    private readonly ConcurrentDictionary<Guid, PipelineStage> _lastLoggedStages = new();

    public PipelineTranslationQueueService(
        IPipelineJobScheduler scheduler,
        TranslationLogService? translationLogs = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _translationLogs = translationLogs;
        _scheduler.ProgressChanged += OnPipelineProgressChanged;
    }

    public event EventHandler<TranslationQueueProgress>? ProgressChanged;

    public async ValueTask<TranslationQueueHandle> EnqueueAsync(
        TranslationQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var jobId = Guid.NewGuid();
        var pipelineRequest = new PipelineRequest(
            request.SourcePath,
            request.OutputPath,
            targetLanguage: request.TargetLanguage,
            styles: new HashSet<TranslationStyle> { request.Style },
            // The source remains immutable. Repacking a signed JAR is allowed only as a clearly
            // unsigned output copy whose signature blocks and stale manifest digests are removed.
            signedArchiveHandling: SignedArchiveHandling.CreateUnsignedCopy,
            hardcodedStringMode: HardcodedStringMode.ExternalizeRecognizedSafePatterns,
            modelSourceId: request.ModelSourceId,
            requestedJobId: jobId);

        TryBeginTranslationLog(
            jobId,
            pipelineRequest.SourcePath,
            request.ModelSourceId);

        PipelineJobHandle pipelineHandle;
        try
        {
            pipelineHandle = await _scheduler
                .EnqueueAsync(pipelineRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _translationLogs?.CompleteSession(
                jobId,
                TranslationLogLevel.Warning,
                "Translation queue request canceled before it was accepted.");
            throw;
        }
        catch (Exception exception)
        {
            _translationLogs?.CompleteSession(
                jobId,
                TranslationLogLevel.Error,
                CreateFailureLogMessage("Translation queue rejected the job", exception));
            throw;
        }

        if (pipelineHandle.JobId != jobId)
        {
            pipelineHandle.Cancel();
            _translationLogs?.CompleteSession(
                jobId,
                TranslationLogLevel.Error,
                "Translation scheduler returned an unexpected job identifier.");
            throw new InvalidDataException("The translation scheduler did not preserve the requested job identifier.");
        }

        if (_translationLogs is not null)
        {
            _translationLogs.TryWrite(
                pipelineHandle.JobId,
                TranslationLogLevel.Debug,
                "Queue",
                "Translation job accepted by the processing queue.");
            _lastLoggedStages.TryRemove(pipelineHandle.JobId, out _);
            LogProgress(pipelineHandle.LatestProgress);
        }

        return new TranslationQueueHandle(
            pipelineHandle.JobId,
            ConvertAndLogResultAsync(
                pipelineHandle.Completion,
                request.Style,
                pipelineRequest.TargetLanguage,
                pipelineHandle.JobId),
            () =>
            {
                _translationLogs?.TryWrite(
                    pipelineHandle.JobId,
                    TranslationLogLevel.Warning,
                    "Queue",
                    "Translation cancellation requested.");
                pipelineHandle.Cancel();
            },
            () => TranslateProgress(pipelineHandle.LatestProgress));
    }

    private void TryBeginTranslationLog(
        Guid jobId,
        string sourcePath,
        string modelSourceId)
    {
        if (_translationLogs is null)
        {
            return;
        }

        try
        {
            _ = _translationLogs.BeginSession(jobId, sourcePath, modelSourceId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Diagnostics are best effort. Their lifecycle must never block or reject a job.
        }
    }

    private async Task<TranslationQueueResult> ConvertAndLogResultAsync(
        Task<PipelineResult> completion,
        TranslationStyle requestedStyle,
        string targetLanguage,
        Guid jobId)
    {
        try
        {
            var result = await ConvertResultAsync(
                    completion,
                    requestedStyle,
                    targetLanguage)
                .ConfigureAwait(false);
            _translationLogs?.CompleteSession(
                jobId,
                TranslationLogLevel.Information,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Translation completed | output={Path.GetFileName(result.OutputPath)} | artifacts={result.ArtifactPaths.Count}"));
            return result;
        }
        catch (OperationCanceledException)
        {
            _translationLogs?.CompleteSession(
                jobId,
                TranslationLogLevel.Warning,
                "Translation canceled.");
            throw;
        }
        catch (Exception exception)
        {
            _translationLogs?.CompleteSession(
                jobId,
                TranslationLogLevel.Error,
                CreateFailureLogMessage("Translation failed", exception));
            throw;
        }
        finally
        {
            _lastLoggedStages.TryRemove(jobId, out _);
        }
    }

    internal static string CreateFailureLogMessage(string operation, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);

        var pipelineException = exception as PipelineException;
        var cause = pipelineException?.InnerException ?? exception;
        var stage = pipelineException?.FailedStage.ToString() ?? "none";
        var modelFailure = cause as ModelServiceException;
        var httpStatus = modelFailure?.StatusCode is { } statusCode
            ? ((int)statusCode).ToString(CultureInfo.InvariantCulture)
            : "none";
        var requestId = modelFailure?.RequestId ?? "none";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{operation} | stage={stage} | type={exception.GetType().Name} | cause={cause.GetType().Name} | " +
            $"http={httpStatus} | request={requestId} | hresult=0x{exception.HResult:X8} | detail={cause.Message}");
    }

    private static async Task<TranslationQueueResult> ConvertResultAsync(
        Task<PipelineResult> completion,
        TranslationStyle requestedStyle,
        string targetLanguage)
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
            requestedStyle,
            targetLanguage,
            result.ModelUsage);
    }

    private void OnPipelineProgressChanged(object? sender, PipelineProgress progress)
    {
        LogProgress(progress);
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
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A view subscriber must not terminate the transactional worker.
            }
        }
    }

    private void LogProgress(PipelineProgress? progress)
    {
        if (_translationLogs is null || progress is null)
        {
            return;
        }

        var nextStage = progress.NextStage?.ToString() ?? "none";
        _translationLogs.TryWrite(
            progress.JobId,
            TranslationLogLevel.Trace,
            "Progress",
            string.Create(
                CultureInfo.InvariantCulture,
                $"stage={progress.Stage} | fraction={progress.Fraction:F4} | next={nextStage}"));

        var stageChanged = !_lastLoggedStages.TryGetValue(progress.JobId, out var previous) ||
            previous != progress.Stage;
        _lastLoggedStages[progress.JobId] = progress.Stage;
        if (!stageChanged)
        {
            return;
        }

        _translationLogs.TryWrite(
            progress.JobId,
            TranslationLogLevel.Debug,
            "Pipeline",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Stage changed | stage={progress.Stage} | next={nextStage}"));
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
            progress.RollbackStatus,
            progress.ModelUsage);
    }
}
