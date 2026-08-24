using LocaleSmith.Application.Models;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.Presentation.Services;

public sealed class InMemoryModProjectWorkspace : IModProjectWorkspace
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ProjectState> _projects = [];
    private readonly Dictionary<string, Guid> _projectIdsBySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, TaskState> _tasks = [];
    private readonly Dictionary<Guid, Guid> _taskIdsByJob = [];
    private Guid? _activeProjectId;

    public event EventHandler<ModProjectWorkspaceChangedEventArgs>? Changed;

    public ModProjectSnapshot? ActiveProject
    {
        get
        {
            lock (_gate)
            {
                return _activeProjectId is { } projectId && _projects.TryGetValue(projectId, out ProjectState? state)
                    ? CreateProjectSnapshot(state)
                    : null;
            }
        }
    }

    public IReadOnlyList<ModProjectSnapshot> Projects
    {
        get
        {
            lock (_gate)
            {
                return _projects.Values
                    .OrderBy(static project => project.CreatedAtUtc)
                    .Select(CreateProjectSnapshot)
                    .ToArray();
            }
        }
    }

    public ModProjectSnapshot RegisterProject(string sourceArtifactPath, bool makeActive = true)
    {
        string normalizedSource = NormalizeSourceArtifactPath(sourceArtifactPath);
        ModProjectSnapshot snapshot;
        bool registered = false;
        bool activated = false;
        lock (_gate)
        {
            if (!_projectIdsBySource.TryGetValue(normalizedSource, out Guid projectId))
            {
                var now = DateTimeOffset.UtcNow;
                projectId = Guid.NewGuid();
                var state = new ProjectState(projectId, normalizedSource, now);
                _projects.Add(projectId, state);
                _projectIdsBySource.Add(normalizedSource, projectId);
                registered = true;
            }

            ProjectState project = _projects[projectId];
            if (makeActive && _activeProjectId != projectId)
            {
                _activeProjectId = projectId;
                project.UpdatedAtUtc = DateTimeOffset.UtcNow;
                activated = true;
            }

            snapshot = CreateProjectSnapshot(project);
        }

        if (registered)
        {
            RaiseChanged(ModProjectWorkspaceChangeKind.ProjectRegistered, snapshot);
        }

        if (activated)
        {
            RaiseChanged(ModProjectWorkspaceChangeKind.ActiveProjectChanged, snapshot);
        }

        return snapshot;
    }

    public bool TrySetActiveProject(Guid projectId, out ModProjectSnapshot? project)
    {
        ModProjectSnapshot? snapshot;
        bool changed;
        lock (_gate)
        {
            if (!_projects.TryGetValue(projectId, out ProjectState? state))
            {
                project = null;
                return false;
            }

            changed = _activeProjectId != projectId;
            _activeProjectId = projectId;
            if (changed)
            {
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            snapshot = CreateProjectSnapshot(state);
            project = snapshot;
        }

        if (changed)
        {
            RaiseChanged(ModProjectWorkspaceChangeKind.ActiveProjectChanged, snapshot);
        }

        return true;
    }

    public bool TryGetProject(Guid projectId, out ModProjectSnapshot? project)
    {
        lock (_gate)
        {
            if (_projects.TryGetValue(projectId, out ProjectState? state))
            {
                project = CreateProjectSnapshot(state);
                return true;
            }

            project = null;
            return false;
        }
    }

    public bool TryGetTask(Guid taskId, out ModProjectTaskSnapshot? task)
    {
        lock (_gate)
        {
            if (_tasks.TryGetValue(taskId, out TaskState? state))
            {
                task = CreateTaskSnapshot(state);
                return true;
            }

            task = null;
            return false;
        }
    }

    public ModProjectTaskSnapshot RegisterTask(
        Guid projectId,
        ModProjectTaskRegistration registration,
        bool rejectIfProjectHasActiveTask = false)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ModProjectTaskSnapshot taskSnapshot;
        ModProjectSnapshot projectSnapshot;
        lock (_gate)
        {
            if (!_projects.TryGetValue(projectId, out ProjectState? project))
            {
                throw new KeyNotFoundException($"Project '{projectId}' is not registered.");
            }

            if (!string.Equals(
                    NormalizeSourceArtifactPath(registration.SourcePath),
                    project.SourceArtifactPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "A project task must use the source artifact registered for its project.",
                    nameof(registration));
            }

            if (rejectIfProjectHasActiveTask && project.TaskIds.Any(taskId => IsActive(_tasks[taskId].Status)))
            {
                throw new InvalidOperationException(
                    "The project already has a registered, queued, or running task.");
            }

            var now = DateTimeOffset.UtcNow;
            var task = new TaskState(Guid.NewGuid(), projectId, registration, now)
            {
                ModId = project.ModId,
                Loader = project.Loader
            };
            _tasks.Add(task.TaskId, task);
            project.TaskIds.Add(task.TaskId);
            project.UpdatedAtUtc = now;
            _activeProjectId = projectId;
            taskSnapshot = CreateTaskSnapshot(task);
            projectSnapshot = CreateProjectSnapshot(project);
        }

        RaiseChanged(ModProjectWorkspaceChangeKind.TaskRegistered, projectSnapshot, taskSnapshot);
        return taskSnapshot;
    }

    public ModProjectTaskSnapshot AttachJob(Guid taskId, Guid jobId, Action cancel)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("A project task job id cannot be empty.", nameof(jobId));
        }

        ArgumentNullException.ThrowIfNull(cancel);
        ModProjectTaskSnapshot taskSnapshot;
        ModProjectSnapshot projectSnapshot;
        lock (_gate)
        {
            TaskState task = GetTaskState(taskId);
            if (task.JobId is not null || task.Status != ModProjectTaskStatus.Registered)
            {
                throw new InvalidOperationException("The project task is already attached or is no longer registrable.");
            }

            if (_taskIdsByJob.ContainsKey(jobId))
            {
                throw new InvalidOperationException("The pipeline job is already attached to another project task.");
            }

            task.JobId = jobId;
            task.Cancel = cancel;
            task.Status = ModProjectTaskStatus.Queued;
            task.Stage = PipelineStage.Queued;
            task.UpdatedAtUtc = DateTimeOffset.UtcNow;
            _taskIdsByJob.Add(jobId, taskId);
            ProjectState project = _projects[task.ProjectId];
            project.UpdatedAtUtc = task.UpdatedAtUtc;
            taskSnapshot = CreateTaskSnapshot(task);
            projectSnapshot = CreateProjectSnapshot(project);
        }

        RaiseChanged(ModProjectWorkspaceChangeKind.JobAttached, projectSnapshot, taskSnapshot);
        return taskSnapshot;
    }

    public bool TryUpdateInspection(
        Guid projectId,
        string modId,
        string loader,
        out ModProjectSnapshot? project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentException.ThrowIfNullOrWhiteSpace(loader);
        ModProjectSnapshot snapshot;
        lock (_gate)
        {
            if (!_projects.TryGetValue(projectId, out ProjectState? state))
            {
                project = null;
                return false;
            }

            state.ModId = modId.Trim();
            state.Loader = loader.Trim();
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            snapshot = CreateProjectSnapshot(state);
            project = snapshot;
        }

        RaiseChanged(ModProjectWorkspaceChangeKind.InspectionUpdated, snapshot);
        return true;
    }

    public bool TryReportProgress(
        Guid jobId,
        TranslationQueueProgress progress,
        out ModProjectTaskSnapshot? task)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ModProjectTaskSnapshot snapshot;
        ModProjectSnapshot projectSnapshot;
        lock (_gate)
        {
            if (!_taskIdsByJob.TryGetValue(jobId, out Guid taskId) || !_tasks.TryGetValue(taskId, out TaskState? state))
            {
                task = null;
                return false;
            }

            if (state.Status is ModProjectTaskStatus.Completed or ModProjectTaskStatus.Failed or ModProjectTaskStatus.Cancelled)
            {
                task = CreateTaskSnapshot(state);
                return false;
            }

            if (progress.JobId != jobId)
            {
                throw new ArgumentException("The progress job id does not match the attached project task.", nameof(progress));
            }

            state.Stage = progress.Stage;
            state.Progress = Math.Clamp(progress.Fraction, 0, 1);
            if (progress.ModelUsage is not null)
            {
                state.ModelUsage = progress.ModelUsage;
            }

            if (progress.Stage != PipelineStage.Queued &&
                (state.Status == ModProjectTaskStatus.Registered ||
                 state.Status == ModProjectTaskStatus.Queued))
            {
                state.Status = ModProjectTaskStatus.Running;
            }

            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            ProjectState project = _projects[state.ProjectId];
            project.UpdatedAtUtc = state.UpdatedAtUtc;
            snapshot = CreateTaskSnapshot(state);
            projectSnapshot = CreateProjectSnapshot(project);
            task = snapshot;
        }

        RaiseChanged(ModProjectWorkspaceChangeKind.ProgressUpdated, projectSnapshot, snapshot);
        return true;
    }

    public bool TryCompleteTask(Guid taskId, TranslationQueueResult result, out ModProjectTaskSnapshot? task)
    {
        ArgumentNullException.ThrowIfNull(result);
        ModProjectTaskSnapshot snapshot;
        ModProjectSnapshot projectSnapshot;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out TaskState? state))
            {
                task = null;
                return false;
            }

            if (state.JobId != result.JobId)
            {
                throw new InvalidOperationException("The translation result does not belong to the project task.");
            }

            if (!string.Equals(
                    Path.GetFullPath(result.OutputPath),
                    state.Registration.OutputPath,
                    StringComparison.OrdinalIgnoreCase) ||
                result.Style != state.Registration.Style ||
                !string.Equals(
                    LocaleSmith.Core.Services.TranslationLanguageCatalog.NormalizeLocale(result.TargetLanguage),
                    state.Registration.TargetLanguage,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The translation result does not match the output, style, or language captured by the project task.");
            }

            var now = DateTimeOffset.UtcNow;
            state.Stage = PipelineStage.Completed;
            state.Progress = 1;
            state.ModId = result.ModId;
            state.Loader = result.Loader;
            state.ArtifactPaths = result.ArtifactPaths.ToArray();
            state.ModelUsage = result.ModelUsage;
            state.Status = ModProjectTaskStatus.Completed;
            state.FailureType = null;
            state.UpdatedAtUtc = now;
            state.FinishedAtUtc = now;
            state.Cancel = null;
            ProjectState project = _projects[state.ProjectId];
            project.ModId = result.ModId;
            project.Loader = result.Loader;
            project.UpdatedAtUtc = now;
            snapshot = CreateTaskSnapshot(state);
            projectSnapshot = CreateProjectSnapshot(project);
            task = snapshot;
        }

        RaiseChanged(ModProjectWorkspaceChangeKind.TaskCompleted, projectSnapshot, snapshot);
        return true;
    }

    public bool TryFailTask(Guid taskId, string? failureType, out ModProjectTaskSnapshot? task) =>
        TryFinishTask(taskId, ModProjectTaskStatus.Failed, PipelineStage.Failed, failureType, out task);

    public bool TryMarkCancelled(Guid taskId, out ModProjectTaskSnapshot? task) =>
        TryFinishTask(taskId, ModProjectTaskStatus.Cancelled, PipelineStage.Cancelled, null, out task);

    public bool TryRequestCancellation(Guid taskId, out ModProjectTaskSnapshot? task)
    {
        Action? cancel;
        ModProjectTaskSnapshot snapshot;
        ModProjectSnapshot projectSnapshot;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out TaskState? state) || !CreateTaskSnapshot(state).IsActive)
            {
                task = null;
                return false;
            }

            cancel = state.Cancel;
            if (cancel is null)
            {
                task = null;
                return false;
            }

            state.Status = ModProjectTaskStatus.CancellationRequested;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            ProjectState project = _projects[state.ProjectId];
            project.UpdatedAtUtc = state.UpdatedAtUtc;
            snapshot = CreateTaskSnapshot(state);
            projectSnapshot = CreateProjectSnapshot(project);
            task = snapshot;
        }

        cancel();
        RaiseChanged(ModProjectWorkspaceChangeKind.CancellationRequested, projectSnapshot, snapshot);
        return true;
    }

    private bool TryFinishTask(
        Guid taskId,
        ModProjectTaskStatus status,
        PipelineStage stage,
        string? failureType,
        out ModProjectTaskSnapshot? task)
    {
        ModProjectTaskSnapshot snapshot;
        ModProjectSnapshot projectSnapshot;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out TaskState? state))
            {
                task = null;
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            state.Stage = stage;
            state.Status = status;
            state.FailureType = string.IsNullOrWhiteSpace(failureType) ? null : failureType.Trim();
            state.UpdatedAtUtc = now;
            state.FinishedAtUtc = now;
            state.Cancel = null;
            ProjectState project = _projects[state.ProjectId];
            project.UpdatedAtUtc = now;
            snapshot = CreateTaskSnapshot(state);
            projectSnapshot = CreateProjectSnapshot(project);
            task = snapshot;
        }

        RaiseChanged(
            status == ModProjectTaskStatus.Cancelled
                ? ModProjectWorkspaceChangeKind.TaskCancelled
                : ModProjectWorkspaceChangeKind.TaskFailed,
            projectSnapshot,
            snapshot);
        return true;
    }

    private TaskState GetTaskState(Guid taskId) => _tasks.TryGetValue(taskId, out TaskState? state)
        ? state
        : throw new KeyNotFoundException($"Project task '{taskId}' is not registered.");

    private ModProjectSnapshot CreateProjectSnapshot(ProjectState state) => new(
        state.ProjectId,
        state.SourceArtifactPath,
        state.ModId,
        state.Loader,
        state.TaskIds.Select(taskId => CreateTaskSnapshot(_tasks[taskId])).ToArray(),
        state.CreatedAtUtc,
        state.UpdatedAtUtc);

    private static ModProjectTaskSnapshot CreateTaskSnapshot(TaskState state) => new(
        state.TaskId,
        state.ProjectId,
        state.Registration.SourcePath,
        state.Registration.OutputPath,
        state.Registration.ModelSourceId,
        state.Registration.TargetLanguage,
        state.Registration.Style,
        state.Registration.Objective,
        state.JobId,
        state.Stage,
        state.Progress,
        state.ModId,
        state.Loader,
        state.ArtifactPaths.ToArray(),
        state.Status,
        state.FailureType,
        state.CreatedAtUtc,
        state.UpdatedAtUtc,
        state.FinishedAtUtc,
        state.ModelUsage);

    private static string NormalizeSourceArtifactPath(string sourceArtifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceArtifactPath);
        string fullPath = Path.GetFullPath(sourceArtifactPath);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static bool IsActive(ModProjectTaskStatus status) => status is
        ModProjectTaskStatus.Registered or
        ModProjectTaskStatus.Queued or
        ModProjectTaskStatus.Running or
        ModProjectTaskStatus.CancellationRequested;

    private void RaiseChanged(
        ModProjectWorkspaceChangeKind kind,
        ModProjectSnapshot project,
        ModProjectTaskSnapshot? task = null)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var args = new ModProjectWorkspaceChangedEventArgs(kind, project, task);
        foreach (EventHandler<ModProjectWorkspaceChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            {
                // Workspace observers must never alter queue or transaction outcomes.
            }
        }
    }

    private sealed class ProjectState(Guid projectId, string sourceArtifactPath, DateTimeOffset createdAtUtc)
    {
        public Guid ProjectId { get; } = projectId;

        public string SourceArtifactPath { get; } = sourceArtifactPath;

        public string? ModId { get; set; }

        public string? Loader { get; set; }

        public List<Guid> TaskIds { get; } = [];

        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;

        public DateTimeOffset UpdatedAtUtc { get; set; } = createdAtUtc;
    }

    private sealed class TaskState(
        Guid taskId,
        Guid projectId,
        ModProjectTaskRegistration registration,
        DateTimeOffset createdAtUtc)
    {
        public Guid TaskId { get; } = taskId;

        public Guid ProjectId { get; } = projectId;

        public ModProjectTaskRegistration Registration { get; } = registration;

        public Guid? JobId { get; set; }

        public PipelineStage Stage { get; set; } = PipelineStage.Queued;

        public double Progress { get; set; }

        public string? ModId { get; set; }

        public string? Loader { get; set; }

        public IReadOnlyList<string> ArtifactPaths { get; set; } = [];

        public ModProjectTaskStatus Status { get; set; } = ModProjectTaskStatus.Registered;

        public string? FailureType { get; set; }

        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;

        public DateTimeOffset UpdatedAtUtc { get; set; } = createdAtUtc;

        public DateTimeOffset? FinishedAtUtc { get; set; }

        public Action? Cancel { get; set; }

        public LocaleSmith.Core.Models.ModelTokenUsage? ModelUsage { get; set; }
    }
}
