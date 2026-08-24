using System.Collections.Concurrent;
using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Models;
using LocaleSmith.Archive;
using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;
using LocaleSmith.Mcp;
using LocaleSmith.NativeInterop;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Services;

public sealed class ProjectMcpBackend : IProjectMcpBackend, IDisposable
{
    private const int MaximumWarnings = 32;
    private const int MaximumWarningCharacters = 1024;
    private readonly IModProjectWorkspace _workspace;
    private readonly IArchiveScanner _archiveScanner;
    private readonly IArchiveWorkspaceBackend _archiveWorkspaceBackend;
    private readonly ITranslationQueueService _translationQueue;
    private readonly IOutputPathStrategy _outputPathStrategy;
    private readonly IModelSelectionService _modelSelection;
    private readonly ConcurrentDictionary<Guid, Guid> _ownedTaskIdsByJob = [];
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private int _disposeState;

    public ProjectMcpBackend(
        IModProjectWorkspace workspace,
        IArchiveScanner archiveScanner,
        ITranslationQueueService translationQueue,
        IOutputPathStrategy outputPathStrategy,
        IModelSelectionService modelSelection,
        IArchiveWorkspaceBackend? archiveWorkspaceBackend = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _archiveScanner = archiveScanner ?? throw new ArgumentNullException(nameof(archiveScanner));
        _archiveWorkspaceBackend = archiveWorkspaceBackend ?? new ArchiveWorkspaceBackend(_archiveScanner);
        _translationQueue = translationQueue ?? throw new ArgumentNullException(nameof(translationQueue));
        _outputPathStrategy = outputPathStrategy ?? throw new ArgumentNullException(nameof(outputPathStrategy));
        _modelSelection = modelSelection ?? throw new ArgumentNullException(nameof(modelSelection));
        _translationQueue.ProgressChanged += OnProgressChanged;
    }

    public ValueTask<ProjectMcpSnapshot?> GetActiveProjectAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ModProjectSnapshot? project = _workspace.ActiveProject;
        return ValueTask.FromResult(project is null ? null : ToMcpProject(project));
    }

    public ValueTask<ProjectMcpSnapshot?> GetProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _workspace.TryGetProject(projectId, out ModProjectSnapshot? project) && project is not null
                ? ToMcpProject(project)
                : null);
    }

    public async ValueTask<ArchiveMcpInspection> InspectArchiveAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ModProjectSnapshot project = GetRequiredActiveProject(projectId);
        bool isFile = File.Exists(project.SourceArtifactPath) && !Directory.Exists(project.SourceArtifactPath);
        bool isDirectory = Directory.Exists(project.SourceArtifactPath);
        if (!isFile && !isDirectory)
        {
            throw new ProjectMcpBackendException(
                "The active project does not reference an available package source.");
        }

        if (isDirectory)
        {
            return await InspectDirectoryAsync(project, cancellationToken).ConfigureAwait(false);
        }

        ArchiveScanManifest manifest;
        try
        {
            manifest = await Task.Run(
                    () => _archiveScanner.ScanArchive(project.SourceArtifactPath),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not AccessViolationException)
        {
            throw new ProjectMcpBackendException(
                "The active project archive could not be inspected safely.",
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        string modId = string.IsNullOrWhiteSpace(manifest.ModMetadata.PrimaryModId)
            ? "unknown"
            : manifest.ModMetadata.PrimaryModId;
        string loader = string.IsNullOrWhiteSpace(manifest.ModMetadata.PrimaryLoader)
            ? "unknown"
            : manifest.ModMetadata.PrimaryLoader;
        _workspace.TryUpdateInspection(projectId, modId, loader, out _);
        return new ArchiveMcpInspection(
            projectId,
            GetSourceName(project.SourceArtifactPath),
            Bound(modId, 256),
            Bound(loader, 128),
            manifest.Archive.EntryCount,
            manifest.Resources.Count,
            Bound(manifest.Archive.Signatures.Status, 128),
            manifest.ModMetadata.UsedFilenameFallback,
            manifest.Warnings
                .Take(MaximumWarnings)
                .Select(static warning => Bound(warning, MaximumWarningCharacters))
                .ToArray());
    }

    private async ValueTask<ArchiveMcpInspection> InspectDirectoryAsync(
        ModProjectSnapshot project,
        CancellationToken cancellationToken)
    {
        string inspectionOutput = Path.Combine(
            Path.GetTempPath(),
            "LocaleSmith",
            "mcp-inspection-output",
            $"{Guid.NewGuid():N}.zip");
        var request = new PipelineRequest(
            project.SourceArtifactPath,
            inspectionOutput,
            hardcodedStringMode: HardcodedStringMode.ScanOnly);
        try
        {
            await using IArchiveWorkspace workspace = await _archiveWorkspaceBackend
                .BeginAsync(Guid.NewGuid(), request, cancellationToken)
                .ConfigureAwait(false);
            ArchiveInspection inspection = await workspace
                .InspectAsync(cancellationToken)
                .ConfigureAwait(false);
            _workspace.TryUpdateInspection(
                project.ProjectId,
                inspection.ModId,
                inspection.Loader,
                out _);
            return new ArchiveMcpInspection(
                project.ProjectId,
                GetSourceName(project.SourceArtifactPath),
                Bound(inspection.ModId, 256),
                Bound(inspection.Loader, 128),
                inspection.EntryCount,
                inspection.ResourceCount,
                Bound(inspection.SignatureStatus, 128),
                inspection.UsedFileNameFallback,
                inspection.Warnings
                    .Take(MaximumWarnings)
                    .Select(static warning => Bound(warning, MaximumWarningCharacters))
                    .ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not AccessViolationException)
        {
            throw new ProjectMcpBackendException(
                "The active project directory could not be inspected through a safe snapshot.",
                exception);
        }
    }

    public async ValueTask<TaskMcpSnapshot> StartTranslationAsync(
        TranslationMcpStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Objective) || request.Objective.Length > 2048)
        {
            throw new ProjectMcpBackendException("A non-empty translation objective of at most 2048 characters is required.");
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ModProjectSnapshot project = GetRequiredActiveProject(request.ProjectId);
            if (project.ActiveTask is not null)
            {
                throw new ProjectMcpBackendException(
                    "The active project already has a running or queued translation task.");
            }

            ModelSource modelSource = ResolveModelSource(request.ModelSourceId, project.LatestTask?.ModelSourceId);
            string targetLanguage;
            try
            {
                targetLanguage = TranslationLanguageCatalog.GetRequired(
                        string.IsNullOrWhiteSpace(request.TargetLanguage)
                            ? project.LatestTask?.TargetLanguage ?? TranslationLanguageCatalog.DefaultLocale
                            : request.TargetLanguage)
                    .CanonicalLocale;
            }
            catch (ArgumentException exception)
            {
                throw new ProjectMcpBackendException(
                    "The requested target language is not supported by LocaleSmith.",
                    exception);
            }
            TranslationStyle style = ResolveStyle(request.Style, project.LatestTask?.Style);
            string outputPath;
            try
            {
                outputPath = await _outputPathStrategy
                    .CreateOutputPathAsync(project.SourceArtifactPath, targetLanguage, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or
                InvalidDataException or
                UnauthorizedAccessException or
                ArgumentException or
                InvalidOperationException or
                NotSupportedException)
            {
                throw new ProjectMcpBackendException(
                    "A safe output path could not be created for the active project.",
                    exception);
            }

            ModProjectTaskSnapshot task;
            try
            {
                task = _workspace.RegisterTask(
                    project.ProjectId,
                    new ModProjectTaskRegistration(
                        project.SourceArtifactPath,
                        outputPath,
                        modelSource.Id,
                        targetLanguage,
                        style,
                        request.Objective),
                    rejectIfProjectHasActiveTask: true);
            }
            catch (InvalidOperationException exception)
            {
                throw new ProjectMcpBackendException(
                    "The active project already has a running or queued translation task.",
                    exception);
            }

            Guid taskId = task.TaskId;
            try
            {
                TranslationQueueHandle handle = await _translationQueue.EnqueueAsync(
                        new TranslationQueueRequest(
                            project.SourceArtifactPath,
                            outputPath,
                            modelSource.Id,
                            style,
                            targetLanguage,
                            modelSource.MaxOutputTokens ?? ModelSource.DefaultMaxOutputTokens,
                            modelSource.MaxSourceCharactersPerRequest ??
                                ModelSource.DefaultMaxSourceCharactersPerRequest),
                        cancellationToken)
                    .ConfigureAwait(false);
                task = _workspace.AttachJob(task.TaskId, handle.JobId, handle.Cancel);
                _ownedTaskIdsByJob[handle.JobId] = taskId;

                if (handle.LatestProgress is { } latestProgress)
                {
                    if (_workspace.TryReportProgress(
                            handle.JobId,
                            latestProgress,
                            out ModProjectTaskSnapshot? updatedTask) &&
                        updatedTask is not null)
                    {
                        task = updatedTask;
                    }
                }

                _ = MonitorAsync(taskId, handle);
                return ToMcpTask(task);
            }
            catch (OperationCanceledException)
            {
                _workspace.TryMarkCancelled(taskId, out _);
                throw;
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and
                not AccessViolationException)
            {
                _workspace.TryFailTask(taskId, exception.GetType().Name, out _);
                throw new ProjectMcpBackendException(
                    "The translation task could not be accepted by the processing queue.",
                    exception);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public ValueTask<TaskMcpSnapshot?> GetTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ModProjectSnapshot active = GetRequiredActiveProject();
        return GetTaskAsync(active.ProjectId, taskId, cancellationToken);
    }

    public ValueTask<TaskMcpSnapshot?> GetTaskAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _workspace.TryGetProject(projectId, out _) &&
            _workspace.TryGetTask(taskId, out ModProjectTaskSnapshot? task) &&
            task is not null &&
            task.ProjectId == projectId
                ? ToMcpTask(task)
                : null);
    }

    public ValueTask<TaskMcpSnapshot> CancelTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ModProjectSnapshot active = GetRequiredActiveProject();
        return CancelTaskAsync(active.ProjectId, taskId, cancellationToken);
    }

    public ValueTask<TaskMcpSnapshot> CancelTaskAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_workspace.TryGetTask(taskId, out ModProjectTaskSnapshot? existing) ||
            existing is null ||
            existing.ProjectId != projectId ||
            !_workspace.TryGetProject(projectId, out _))
        {
            throw new ProjectMcpBackendException(
                "The task was not found in the active LocaleSmith project.");
        }

        if (!_workspace.TryRequestCancellation(taskId, out ModProjectTaskSnapshot? task) || task is null)
        {
            throw new ProjectMcpBackendException(
                "The task is not active or does not have a cancellable queue handle.");
        }

        return ValueTask.FromResult(ToMcpTask(task));
    }

    private async Task MonitorAsync(Guid taskId, TranslationQueueHandle handle)
    {
        try
        {
            TranslationQueueResult result = await handle.Completion.ConfigureAwait(false);
            _workspace.TryCompleteTask(taskId, result, out _);
        }
        catch (OperationCanceledException)
        {
            _workspace.TryMarkCancelled(taskId, out _);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not AccessViolationException)
        {
            _workspace.TryFailTask(taskId, exception.GetType().Name, out _);
        }
        finally
        {
            _ownedTaskIdsByJob.TryRemove(handle.JobId, out _);
        }
    }

    private void OnProgressChanged(object? sender, TranslationQueueProgress progress)
    {
        if (_ownedTaskIdsByJob.ContainsKey(progress.JobId))
        {
            _workspace.TryReportProgress(progress.JobId, progress, out _);
        }
    }

    private ModProjectSnapshot GetRequiredActiveProject(Guid? requiredProjectId = null)
    {
        ModProjectSnapshot? active = _workspace.ActiveProject;
        if (active is null)
        {
            throw new ProjectMcpBackendException(
                "No active LocaleSmith project is available. Add or select a package in the application first.");
        }

        if (requiredProjectId is { } projectId && projectId != active.ProjectId)
        {
            throw new ProjectMcpBackendException(
                "The opaque project id does not identify the active LocaleSmith project.");
        }

        return active;
    }

    private ModelSource ResolveModelSource(string? requestedSourceId, string? previousSourceId)
    {
        string? sourceId = string.IsNullOrWhiteSpace(requestedSourceId)
            ? previousSourceId ?? _modelSelection.SelectedSource?.Id
            : requestedSourceId.Trim();
        ModelSource? source = sourceId is null
            ? null
            : _modelSelection.Sources.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, sourceId, StringComparison.Ordinal));
        return source ?? throw new ProjectMcpBackendException(
            "The requested model source is unavailable. Select a configured model source in the application.");
    }

    private static TranslationStyle ResolveStyle(string? value, TranslationStyle? previousStyle)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return previousStyle ?? TranslationStyle.Formal;
        }

        if (Enum.TryParse(value, ignoreCase: true, out TranslationStyle parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new ProjectMcpBackendException("Translation style must be 'formal' or 'informal'.");
    }

    private static ProjectMcpSnapshot ToMcpProject(ModProjectSnapshot project) => new(
        project.ProjectId,
        GetSourceName(project.SourceArtifactPath),
        project.ModId,
        project.Loader,
        project.ActiveTask?.TaskId,
        project.ActiveTask?.Status.ToString());

    private static TaskMcpSnapshot ToMcpTask(ModProjectTaskSnapshot task) => new(
        task.TaskId,
        task.ProjectId,
        task.JobId,
        task.Objective,
        task.ModelSourceId,
        task.TargetLanguage,
        task.Style.ToString(),
        task.Stage.ToString(),
        task.Progress,
        task.Status.ToString(),
        task.ModId,
        task.Loader,
        task.ArtifactPaths.Select(GetSourceName).ToArray(),
        task.FailureType,
        task.ModelUsage?.InputTokens,
        task.ModelUsage?.OutputTokens,
        task.ModelUsage?.TotalTokens,
        task.ModelUsage?.ProviderCallCount ?? 0,
        task.ModelUsage?.IsComplete == true);

    private static string GetSourceName(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "artifact" : Bound(name, 512);
    }

    private static string Bound(string value, int maximumCharacters) => value.Length <= maximumCharacters
        ? value
        : value[..maximumCharacters];

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposeState) != 0,
        this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _translationQueue.ProgressChanged -= OnProgressChanged;
        _startGate.Dispose();
    }
}
