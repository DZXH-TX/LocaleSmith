using JaxI18n.Application.Abstractions;
using JaxI18n.Application.Models;
using JaxI18n.Core.Models;
using JaxI18n.Core.Services;

namespace JaxI18n.Application.Services;

public sealed class TranslationPipeline
{
    private static readonly IReadOnlyList<string> ScanOnlyWarnings =
        new[] { "硬编码文本仅完成候选分析；未修改字节码。" };

    private readonly IArchiveWorkspaceBackend _workspaceBackend;
    private readonly ITranslationEngine _translationEngine;
    private readonly ITranslationMemoryStore _translationMemory;

    public TranslationPipeline(
        IArchiveWorkspaceBackend workspaceBackend,
        ITranslationEngine translationEngine,
        ITranslationMemoryStore translationMemory)
    {
        ArgumentNullException.ThrowIfNull(workspaceBackend);
        ArgumentNullException.ThrowIfNull(translationEngine);
        ArgumentNullException.ThrowIfNull(translationMemory);
        _workspaceBackend = workspaceBackend;
        _translationEngine = translationEngine;
        _translationMemory = translationMemory;
    }

    public async Task<PipelineResult> ExecuteAsync(
        PipelineRequest request,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(request, Guid.NewGuid(), progress, cancellationToken).ConfigureAwait(false);

    public async Task<PipelineResult> ExecuteAsync(
        PipelineRequest request,
        Guid jobId,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("A pipeline job id cannot be empty.", nameof(jobId));
        }

        var stage = PipelineStage.Queued;
        IArchiveWorkspace? workspace = null;
        var progressTracker = new PipelineProgressTracker(jobId, progress);
        progressTracker.Advance(stage, "任务已进入处理队列");

        try
        {
            progressTracker.Advance(stage = PipelineStage.Inspecting, "正在检查归档与模组元数据");
            workspace = await _workspaceBackend
                .BeginAsync(jobId, request, cancellationToken)
                .ConfigureAwait(false);
            var inspection = await workspace.InspectAsync(cancellationToken).ConfigureAwait(false);
            EnforceSignaturePolicy(inspection, request);

            progressTracker.Advance(stage = PipelineStage.Extracting, "正在事务工作区中安全解包");
            await workspace.ExtractAsync(cancellationToken).ConfigureAwait(false);

            progressTracker.Advance(stage = PipelineStage.Analyzing, "正在提取语言资源与硬编码候选项");
            var candidates = await workspace
                .ScanHardcodedStringsAsync(cancellationToken)
                .ConfigureAwait(false);
            var externalization = await HandleExternalizationAsync(
                    workspace,
                    candidates,
                    request.HardcodedStringMode,
                    cancellationToken)
                .ConfigureAwait(false);
            // Externalization runs before the source inventory is frozen. A verified rewrite adds
            // its original literal as a normal TranslationEntry so it participates in the same
            // incremental cache and the captured translation-style request as resource-backed text.
            var entries = await workspace
                .ReadTranslatableEntriesAsync(cancellationToken)
                .ConfigureAwait(false);

            var memoryKey = new TranslationMemoryKey(
                inspection.PackageIdentity,
                request.TargetLanguage,
                request.ModelSourceId,
                _translationEngine.TranslationContractVersion).Normalize();
            var previous = await _translationMemory.LoadAsync(memoryKey, cancellationToken).ConfigureAwait(false);
            var (pending, reused) = SelectPendingEntries(entries, previous, request.Styles);

            IReadOnlyList<TranslatedEntry> newTranslations = Array.Empty<TranslatedEntry>();
            if (pending.Count > 0)
            {
                progressTracker.Advance(
                    stage = PipelineStage.Translating,
                    $"正在翻译 {pending.Count} 个新增或变更条目");
                var translated = await _translationEngine
                    .TranslateAsync(
                        new TranslationBatchRequest(
                            pending,
                            request.TargetLanguage,
                            request.Styles,
                            request.ModelSourceId),
                        cancellationToken)
                    .ConfigureAwait(false);
                newTranslations = translated.Entries;
            }
            else
            {
                progressTracker.Skip(PipelineStage.Translating);
            }

            var merged = MergeTranslations(entries, previous, newTranslations);
            var batchResult = new TranslationBatchResult(request.TargetLanguage, merged);

            progressTracker.Advance(stage = PipelineStage.Writing, "正在写入所选风格的语言资源");
            await workspace.ApplyTranslationsAsync(batchResult, cancellationToken).ConfigureAwait(false);

            progressTracker.Advance(stage = PipelineStage.Repacking, "正在暂存重建后的归档");
            var verification = await workspace
                .StagePackageAsync(request.OutputPath, cancellationToken)
                .ConfigureAwait(false);
            progressTracker.Advance(stage = PipelineStage.Verifying, "正在验证归档结构与关键元数据");
            if (!verification.IsValidArchive || !verification.MetadataPreserved || verification.Errors.Count > 0)
            {
                throw new PipelineException(
                    jobId,
                    PipelineStage.Verifying,
                    $"Package verification failed: {string.Join("; ", verification.Errors)}");
            }

            var artifacts = verification.Artifacts is { Count: > 0 }
                ? verification.Artifacts
                : CreateDefaultArtifacts(request);
            var requestedStyle = request.Styles.Single();
            if (artifacts.Count != 1 || artifacts[0].Style != requestedStyle)
            {
                throw new PipelineException(
                    jobId,
                    PipelineStage.Verifying,
                    "A pipeline job must stage exactly one artifact in its captured translation style.");
            }

            progressTracker.Advance(stage = PipelineStage.Committing, "正在原子提交输出文件");
            await workspace.CommitAsync(cancellationToken).ConfigureAwait(false);

            // The cache must never get ahead of the durable package commit. A cache failure after
            // commit only causes safe retranslation on the next run and must not misreport the
            // already-committed artifacts as rolled back.
            var resultWarnings = new List<string>();
            try
            {
                await _translationMemory
                    .SaveAsync(
                        new TranslationMemorySnapshot(
                            memoryKey,
                            merged.ToDictionary(GetStableId, StringComparer.Ordinal)),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and
                not AccessViolationException)
            {
                resultWarnings.Add(
                    $"输出已提交，但增量翻译缓存未更新；下次可能重新翻译未缓存条目。{exception.GetType().Name}: {exception.Message}");
            }

            progressTracker.Finish(
                PipelineStage.Completed,
                resultWarnings.Count == 0 ? "处理完成" : "处理完成，但增量缓存未更新");
            return new PipelineResult(
                jobId,
                request.OutputPath,
                inspection,
                entries.Count,
                newTranslations.Count,
                reused,
                candidates,
                externalization,
                artifacts,
                verification,
                resultWarnings.AsReadOnly());
        }
        catch (OperationCanceledException)
        {
            if (workspace is not null)
            {
                progressTracker.BeginRollback(PipelineStageStatus.Cancelled, "任务已取消，正在回滚");
                var rollbackSucceeded = await RollbackWithoutMaskingAsync(workspace).ConfigureAwait(false);
                progressTracker.Finish(
                    PipelineStage.Cancelled,
                    rollbackSucceeded ? "任务已取消并完成回滚" : "任务已取消，但回滚未完成",
                    rollbackSucceeded);
            }
            else
            {
                progressTracker.Finish(PipelineStage.Cancelled, "任务已取消");
            }

            throw;
        }
        catch (Exception exception) when (exception is not PipelineException)
        {
            if (workspace is not null)
            {
                progressTracker.BeginRollback(PipelineStageStatus.Failed, "处理失败，正在回滚");
                var rollbackSucceeded = await RollbackWithoutMaskingAsync(workspace).ConfigureAwait(false);
                progressTracker.Finish(
                    PipelineStage.Failed,
                    rollbackSucceeded ? "处理失败，已完成回滚" : "处理失败，且回滚未完成",
                    rollbackSucceeded);
            }
            else
            {
                progressTracker.Finish(PipelineStage.Failed, "处理失败");
            }

            throw new PipelineException(jobId, stage, "The translation pipeline failed and was rolled back.", exception);
        }
        catch
        {
            if (workspace is not null)
            {
                progressTracker.BeginRollback(PipelineStageStatus.Failed, "处理失败，正在回滚");
                var rollbackSucceeded = await RollbackWithoutMaskingAsync(workspace).ConfigureAwait(false);
                progressTracker.Finish(
                    PipelineStage.Failed,
                    rollbackSucceeded ? "处理失败，已完成回滚" : "处理失败，且回滚未完成",
                    rollbackSucceeded);
            }
            else
            {
                progressTracker.Finish(PipelineStage.Failed, "处理失败");
            }

            throw;
        }
        finally
        {
            if (workspace is not null)
            {
                await workspace.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<ExternalizationReport> HandleExternalizationAsync(
        IArchiveWorkspace workspace,
        IReadOnlyList<HardcodedStringCandidate> candidates,
        HardcodedStringMode mode,
        CancellationToken cancellationToken)
    {
        if (mode == HardcodedStringMode.ScanOnly)
        {
            return new ExternalizationReport(
                candidates.Count,
                0,
                ScanOnlyWarnings);
        }

        var safeCandidates = candidates
            .Where(static candidate => candidate.IsRecognizedSafePattern)
            .ToArray();
        return await workspace
            .ExternalizeAsync(safeCandidates, cancellationToken)
            .ConfigureAwait(false);
    }

    private static (IReadOnlyList<TranslationEntry> Pending, int Reused) SelectPendingEntries(
        IReadOnlyList<TranslationEntry> entries,
        TranslationMemorySnapshot previous,
        IReadOnlySet<TranslationStyle> requiredStyles)
    {
        var hashes = previous.Entries.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.SourceHash,
            StringComparer.Ordinal);
        var plan = IncrementalTranslationPlanner.Create(entries, hashes);
        var pendingIds = plan.PendingEntries
            .Select(static entry => entry.StableId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!previous.Entries.TryGetValue(entry.StableId, out var oldEntry) ||
                requiredStyles.Any(style => oldEntry.Variants.All(variant => variant.Style != style)))
            {
                pendingIds.Add(entry.StableId);
            }
        }

        var pending = entries.Where(entry => pendingIds.Contains(entry.StableId)).ToArray();
        return (pending, entries.Count - pending.Length);
    }

    private static List<TranslatedEntry> MergeTranslations(
        IReadOnlyList<TranslationEntry> sourceEntries,
        TranslationMemorySnapshot previous,
        IReadOnlyList<TranslatedEntry> newTranslations)
    {
        var additions = newTranslations.ToDictionary(GetStableId, StringComparer.Ordinal);
        var merged = new List<TranslatedEntry>(sourceEntries.Count);
        foreach (var source in sourceEntries)
        {
            if (additions.TryGetValue(source.StableId, out var updated))
            {
                if (previous.Entries.TryGetValue(source.StableId, out var existing) &&
                    string.Equals(existing.SourceHash, updated.SourceHash, StringComparison.Ordinal))
                {
                    var variants = existing.Variants
                        .Concat(updated.Variants)
                        .GroupBy(static variant => variant.Style)
                        .Select(static group => group.Last())
                        .OrderBy(static variant => variant.Style)
                        .ToArray();
                    merged.Add(updated with { Variants = variants });
                }
                else
                {
                    // A changed source invalidates every older style, even if this job requested only one.
                    merged.Add(updated);
                }
            }
            else if (previous.Entries.TryGetValue(source.StableId, out var existing))
            {
                merged.Add(existing);
            }
            else
            {
                throw new TranslationContractException(
                    $"No translation was produced for '{source.RelativePath}' / '{source.Key}'.");
            }
        }

        return merged;
    }

    private static string GetStableId(TranslatedEntry entry) =>
        $"{entry.RelativePath}\0{entry.Key ?? string.Empty}";

    private static List<PackageArtifact> CreateDefaultArtifacts(PipelineRequest request)
    {
        var artifacts = new List<PackageArtifact>(request.Styles.Count);
        var extension = Path.GetExtension(request.OutputPath);
        var stem = request.OutputPath[..^extension.Length];
        foreach (var style in request.Styles.Order())
        {
            var path = artifacts.Count == 0
                ? request.OutputPath
                : $"{stem}.{style.ToString().ToLowerInvariant()}{extension}";
            artifacts.Add(new PackageArtifact(style, path));
        }

        return artifacts;
    }

    private static void EnforceSignaturePolicy(
        ArchiveInspection inspection,
        PipelineRequest request)
    {
        if (!inspection.IsSigned)
        {
            return;
        }

        if (request.SignedArchiveHandling == SignedArchiveHandling.Block)
        {
            throw new InvalidOperationException(
                "The archive contains Java signature metadata. Repacking would invalidate the signature, so modification is blocked by default.");
        }

        if (request.SignedArchiveHandling == SignedArchiveHandling.Resign && !inspection.CanResign)
        {
            throw new InvalidOperationException(
                "Re-signing was requested, but no signing configuration is available.");
        }
    }

    private static async Task<bool> RollbackWithoutMaskingAsync(IArchiveWorkspace workspace)
    {
        try
        {
            await workspace.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // The original failure remains primary. Backends must persist rollback failures in their audit log.
            return false;
        }
    }

    private sealed class PipelineProgressTracker
    {
        private static readonly PipelineStage[] AllStages = Enum.GetValues<PipelineStage>();
        private static readonly PipelineStage[] TimelineStages =
        [
            PipelineStage.Queued,
            PipelineStage.Inspecting,
            PipelineStage.Extracting,
            PipelineStage.Analyzing,
            PipelineStage.Translating,
            PipelineStage.Writing,
            PipelineStage.Repacking,
            PipelineStage.Verifying,
            PipelineStage.Committing
        ];
        private static readonly PipelineStage[] WorkStages =
        [
            PipelineStage.Inspecting,
            PipelineStage.Extracting,
            PipelineStage.Analyzing,
            PipelineStage.Translating,
            PipelineStage.Writing,
            PipelineStage.Repacking,
            PipelineStage.Verifying,
            PipelineStage.Committing
        ];

        private readonly Guid _jobId;
        private readonly IProgress<PipelineProgress>? _progress;
        private readonly Dictionary<PipelineStage, MutableStageProgress> _stages = AllStages.ToDictionary(
            static stage => stage,
            static stage => new MutableStageProgress(stage));
        private PipelineStage? _currentStage;
        private PipelineStageStatus? _rollbackOutcome;

        public PipelineProgressTracker(Guid jobId, IProgress<PipelineProgress>? progress)
        {
            _jobId = jobId;
            _progress = progress;
        }

        public void Advance(PipelineStage stage, string action)
        {
            var now = TimeProvider.System.GetUtcNow();
            FinishCurrent(PipelineStageStatus.Completed, now);
            var target = _stages[stage];
            target.Status = PipelineStageStatus.Current;
            target.StartedAtUtc ??= now;
            target.FinishedAtUtc = null;
            _currentStage = stage;
            Publish(stage, action);
        }

        public void Skip(PipelineStage stage)
        {
            var target = _stages[stage];
            if (target.Status != PipelineStageStatus.Pending)
            {
                return;
            }

            target.Status = PipelineStageStatus.Skipped;
            target.FinishedAtUtc = TimeProvider.System.GetUtcNow();
        }

        public void BeginRollback(PipelineStageStatus outcome, string action)
        {
            if (outcome is not (PipelineStageStatus.Failed or PipelineStageStatus.Cancelled))
            {
                throw new ArgumentOutOfRangeException(nameof(outcome));
            }

            var now = TimeProvider.System.GetUtcNow();
            FinishCurrent(outcome, now);
            foreach (var stage in WorkStages)
            {
                var pending = _stages[stage];
                if (pending.Status == PipelineStageStatus.Pending)
                {
                    pending.Status = outcome == PipelineStageStatus.Cancelled
                        ? PipelineStageStatus.Cancelled
                        : PipelineStageStatus.Skipped;
                    pending.FinishedAtUtc = now;
                }
            }

            _rollbackOutcome = outcome;
            var rollback = _stages[PipelineStage.RollingBack];
            rollback.Status = PipelineStageStatus.Current;
            rollback.StartedAtUtc = now;
            rollback.FinishedAtUtc = null;
            _currentStage = PipelineStage.RollingBack;
            Publish(PipelineStage.RollingBack, action);
        }

        public void Finish(PipelineStage terminalStage, string action, bool rollbackSucceeded = true)
        {
            if (terminalStage is not (PipelineStage.Completed or PipelineStage.Failed or PipelineStage.Cancelled))
            {
                throw new ArgumentOutOfRangeException(nameof(terminalStage));
            }

            var now = TimeProvider.System.GetUtcNow();
            if (_currentStage == PipelineStage.RollingBack)
            {
                FinishCurrent(
                    rollbackSucceeded ? PipelineStageStatus.Completed : PipelineStageStatus.Failed,
                    now);
            }
            else
            {
                FinishCurrent(terminalStage switch
                {
                    PipelineStage.Completed => PipelineStageStatus.Completed,
                    PipelineStage.Cancelled => PipelineStageStatus.Cancelled,
                    _ => PipelineStageStatus.Failed
                }, now);
            }

            // Only the user-visible workflow is finalized here. RollingBack remains Pending unless BeginRollback
            // actually ran, and terminal outcomes are represented by PipelineProgress.Stage rather than fake steps.
            foreach (var stage in TimelineStages)
            {
                var pending = _stages[stage];
                if (pending.Status != PipelineStageStatus.Pending)
                {
                    continue;
                }

                pending.Status = terminalStage == PipelineStage.Cancelled
                    ? PipelineStageStatus.Cancelled
                    : PipelineStageStatus.Skipped;
                pending.FinishedAtUtc = now;
            }

            var terminal = _stages[terminalStage];
            terminal.Status = terminalStage switch
            {
                PipelineStage.Completed => PipelineStageStatus.Completed,
                PipelineStage.Cancelled => PipelineStageStatus.Cancelled,
                _ => PipelineStageStatus.Failed
            };
            terminal.StartedAtUtc ??= now;
            terminal.FinishedAtUtc = now;
            _currentStage = null;
            Publish(terminalStage, action);
        }

        private void FinishCurrent(PipelineStageStatus status, DateTimeOffset finishedAtUtc)
        {
            if (_currentStage is not { } currentStage)
            {
                return;
            }

            var current = _stages[currentStage];
            current.Status = status;
            current.FinishedAtUtc = finishedAtUtc;
            _currentStage = null;
        }

        private void Publish(PipelineStage stage, string action)
        {
            if (_progress is null)
            {
                return;
            }

            var snapshotStages = _stages[PipelineStage.RollingBack].Status is
                PipelineStageStatus.Pending or PipelineStageStatus.Skipped
                ? TimelineStages
                : [.. TimelineStages, PipelineStage.RollingBack];
            var snapshots = snapshotStages
                .Select(stageKey => _stages[stageKey].Snapshot())
                .ToArray();
            var rollbackStatus = _stages[PipelineStage.RollingBack].Status;
            _progress.Report(new PipelineProgress(
                _jobId,
                stage,
                CalculateFraction(stage),
                action,
                FindNextStage(stage),
                snapshots,
                rollbackStatus is PipelineStageStatus.Pending or PipelineStageStatus.Skipped
                    ? null
                    : rollbackStatus));
        }

        private double CalculateFraction(PipelineStage stage)
        {
            if (stage == PipelineStage.Completed)
            {
                return 1;
            }

            var finished = WorkStages.Count(stageKey => _stages[stageKey].Status is
                PipelineStageStatus.Completed or PipelineStageStatus.Skipped);
            return (double)finished / WorkStages.Length;
        }

        private PipelineStage? FindNextStage(PipelineStage stage)
        {
            if (stage == PipelineStage.RollingBack)
            {
                return _rollbackOutcome == PipelineStageStatus.Cancelled
                    ? PipelineStage.Cancelled
                    : PipelineStage.Failed;
            }

            if (stage is PipelineStage.Completed or PipelineStage.Failed or PipelineStage.Cancelled)
            {
                return null;
            }

            var startIndex = Array.IndexOf(WorkStages, stage);
            for (var index = Math.Max(0, startIndex + 1); index < WorkStages.Length; index++)
            {
                var candidate = _stages[WorkStages[index]];
                if (candidate.Status == PipelineStageStatus.Pending)
                {
                    return candidate.Stage;
                }
            }

            return PipelineStage.Completed;
        }

        private sealed class MutableStageProgress(PipelineStage stage)
        {
            public PipelineStage Stage { get; } = stage;

            public PipelineStageStatus Status { get; set; } = PipelineStageStatus.Pending;

            public DateTimeOffset? StartedAtUtc { get; set; }

            public DateTimeOffset? FinishedAtUtc { get; set; }

            public PipelineStageProgress Snapshot() => new(
                Stage,
                Status,
                StartedAtUtc,
                FinishedAtUtc);
        }
    }
}
