namespace LocaleSmith.Mcp;

/// <summary>
/// Optional application-owned backend for project-scoped domain tools. The standalone stdio host
/// intentionally omits this service, so it cannot create a second project or credential state.
/// </summary>
public interface IProjectMcpBackend
{
    ValueTask<ProjectMcpSnapshot?> GetActiveProjectAsync(CancellationToken cancellationToken = default);

    ValueTask<ProjectMcpSnapshot?> GetProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    ValueTask<ArchiveMcpInspection> InspectArchiveAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    ValueTask<TaskMcpSnapshot> StartTranslationAsync(
        TranslationMcpStartRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<TaskMcpSnapshot?> GetTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    ValueTask<TaskMcpSnapshot?> GetTaskAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default);

    ValueTask<TaskMcpSnapshot> CancelTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    ValueTask<TaskMcpSnapshot> CancelTaskAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectMcpSnapshot(
    Guid ProjectId,
    string SourceName,
    string? ModId,
    string? Loader,
    Guid? ActiveTaskId,
    string? ActiveTaskStatus);

public sealed record ArchiveMcpInspection(
    Guid ProjectId,
    string SourceName,
    string ModId,
    string Loader,
    ulong EntryCount,
    int ResourceCount,
    string SignatureStatus,
    bool UsedFilenameFallback,
    IReadOnlyList<string> Warnings);

public sealed record TranslationMcpStartRequest(
    Guid ProjectId,
    string Objective,
    string? ModelSourceId = null,
    string? TargetLanguage = null,
    string? Style = null);

public sealed record TaskMcpSnapshot(
    Guid TaskId,
    Guid ProjectId,
    Guid? JobId,
    string Objective,
    string ModelSourceId,
    string TargetLanguage,
    string Style,
    string Stage,
    double Progress,
    string Status,
    string? ModId,
    string? Loader,
    IReadOnlyList<string> ArtifactNames,
    string? FailureType,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    int ProviderCallCount,
    bool UsageComplete);

public sealed class ProjectMcpBackendException : Exception
{
    public ProjectMcpBackendException(string message)
        : base(message)
    {
    }

    public ProjectMcpBackendException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
