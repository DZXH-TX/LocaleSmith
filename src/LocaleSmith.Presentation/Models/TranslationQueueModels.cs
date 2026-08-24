using LocaleSmith.Application.Models;
using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;

namespace LocaleSmith.Presentation.Models;

public sealed record TranslationQueueRequest(
    string SourcePath,
    string OutputPath,
    string ModelSourceId,
    TranslationStyle Style = TranslationStyle.Formal,
    string TargetLanguage = TranslationLanguageCatalog.DefaultLocale,
    int MaxOutputTokens = ModelSource.DefaultMaxOutputTokens,
    int MaxSourceCharactersPerRequest = ModelSource.DefaultMaxSourceCharactersPerRequest);

public sealed record TranslationQueueResult(
    Guid JobId,
    string OutputPath,
    string ModId,
    string Loader,
    IReadOnlyList<string> ArtifactPaths,
    IReadOnlyList<HardcodedStringCandidate> HardcodedCandidates,
    int ExternalizedCount,
    TranslationStyle Style = TranslationStyle.Formal,
    string TargetLanguage = TranslationLanguageCatalog.DefaultLocale,
    ModelTokenUsage? ModelUsage = null);

public sealed class TranslationQueueHandle(
    Guid jobId,
    Task<TranslationQueueResult> completion,
    Action cancel,
    Func<TranslationQueueProgress?>? getLatestProgress = null)
{
    private readonly Action _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
    private readonly Func<TranslationQueueProgress?> _getLatestProgress =
        getLatestProgress ?? (() => null);

    public Guid JobId { get; } = jobId;

    public Task<TranslationQueueResult> Completion { get; } =
        completion ?? throw new ArgumentNullException(nameof(completion));

    public TranslationQueueProgress? LatestProgress => _getLatestProgress();

    public void Cancel() => _cancel();
}

public sealed record TranslationQueueProgress(
    Guid JobId,
    PipelineStage Stage,
    double Fraction,
    PipelineStage? NextStage = null,
    IReadOnlyList<PipelineStageProgress>? Stages = null,
    PipelineStageStatus? RollbackStatus = null,
    ModelTokenUsage? ModelUsage = null);

public sealed record ModelAssistantCompletion(
    string Content,
    IReadOnlyList<LocaleSmith.Core.Models.CliCommand> ProposedCommands,
    ModelTokenUsage? ModelUsage = null,
    string? Model = null);
