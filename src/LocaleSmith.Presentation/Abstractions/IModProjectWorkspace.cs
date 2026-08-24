using LocaleSmith.Presentation.Models;

namespace LocaleSmith.Presentation.Abstractions;

/// <summary>
/// Process-lifetime project and task state shared by the queue, assistant context, and MCP tools.
/// This contract deliberately makes no cross-process or restart-persistence guarantee.
/// </summary>
public interface IModProjectWorkspace
{
    event EventHandler<ModProjectWorkspaceChangedEventArgs>? Changed;

    ModProjectSnapshot? ActiveProject { get; }

    IReadOnlyList<ModProjectSnapshot> Projects { get; }

    ModProjectSnapshot RegisterProject(string sourceArtifactPath, bool makeActive = true);

    bool TrySetActiveProject(Guid projectId, out ModProjectSnapshot? project);

    bool TryGetProject(Guid projectId, out ModProjectSnapshot? project);

    bool TryGetTask(Guid taskId, out ModProjectTaskSnapshot? task);

    ModProjectTaskSnapshot RegisterTask(
        Guid projectId,
        ModProjectTaskRegistration registration,
        bool rejectIfProjectHasActiveTask = false);

    ModProjectTaskSnapshot AttachJob(Guid taskId, Guid jobId, Action cancel);

    bool TryUpdateInspection(
        Guid projectId,
        string modId,
        string loader,
        out ModProjectSnapshot? project);

    bool TryReportProgress(Guid jobId, TranslationQueueProgress progress, out ModProjectTaskSnapshot? task);

    bool TryCompleteTask(Guid taskId, TranslationQueueResult result, out ModProjectTaskSnapshot? task);

    bool TryFailTask(Guid taskId, string? failureType, out ModProjectTaskSnapshot? task);

    bool TryMarkCancelled(Guid taskId, out ModProjectTaskSnapshot? task);

    bool TryRequestCancellation(Guid taskId, out ModProjectTaskSnapshot? task);
}
