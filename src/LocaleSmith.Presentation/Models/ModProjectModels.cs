using LocaleSmith.Application.Models;
using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;

namespace LocaleSmith.Presentation.Models;

public enum ModProjectTaskStatus
{
    Registered,
    Queued,
    Running,
    CancellationRequested,
    Completed,
    Failed,
    Cancelled
}

public enum ModProjectWorkspaceChangeKind
{
    ProjectRegistered,
    ActiveProjectChanged,
    InspectionUpdated,
    TaskRegistered,
    JobAttached,
    ProgressUpdated,
    CancellationRequested,
    TaskCompleted,
    TaskFailed,
    TaskCancelled
}

public sealed record ModProjectTaskRegistration
{
    public ModProjectTaskRegistration(
        string sourcePath,
        string outputPath,
        string modelSourceId,
        string targetLanguage,
        TranslationStyle style,
        string objective)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelSourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        if (objective.Length > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(objective),
                "A project task objective cannot exceed 4096 characters.");
        }
        if (!Enum.IsDefined(style))
        {
            throw new ArgumentOutOfRangeException(nameof(style));
        }

        SourcePath = Path.GetFullPath(sourcePath);
        OutputPath = Path.GetFullPath(outputPath);
        ModelSourceId = modelSourceId.Trim();
        TargetLanguage = TranslationLanguageCatalog.NormalizeLocale(targetLanguage);
        Style = style;
        Objective = objective.Trim();
    }

    public string SourcePath { get; }

    public string OutputPath { get; }

    public string ModelSourceId { get; }

    public string TargetLanguage { get; }

    public TranslationStyle Style { get; }

    public string Objective { get; }
}

public sealed record ModProjectTaskSnapshot(
    Guid TaskId,
    Guid ProjectId,
    string SourcePath,
    string OutputPath,
    string ModelSourceId,
    string TargetLanguage,
    TranslationStyle Style,
    string Objective,
    Guid? JobId,
    PipelineStage Stage,
    double Progress,
    string? ModId,
    string? Loader,
    IReadOnlyList<string> ArtifactPaths,
    ModProjectTaskStatus Status,
    string? FailureType,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    ModelTokenUsage? ModelUsage = null)
{
    public bool IsActive => Status is
        ModProjectTaskStatus.Registered or
        ModProjectTaskStatus.Queued or
        ModProjectTaskStatus.Running or
        ModProjectTaskStatus.CancellationRequested;
}

public sealed record ModProjectSnapshot(
    Guid ProjectId,
    string SourceArtifactPath,
    string? ModId,
    string? Loader,
    IReadOnlyList<ModProjectTaskSnapshot> Tasks,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public ModProjectTaskSnapshot? ActiveTask => Tasks.LastOrDefault(static task => task.IsActive);

    public ModProjectTaskSnapshot? LatestTask => Tasks.Count == 0 ? null : Tasks[^1];
}

public sealed class ModProjectWorkspaceChangedEventArgs(
    ModProjectWorkspaceChangeKind kind,
    ModProjectSnapshot project,
    ModProjectTaskSnapshot? task = null) : EventArgs
{
    public ModProjectWorkspaceChangeKind Kind { get; } = kind;

    public ModProjectSnapshot Project { get; } = project ?? throw new ArgumentNullException(nameof(project));

    public ModProjectTaskSnapshot? Task { get; } = task;
}
