using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocaleSmith.Application.Models;
using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.Presentation.ViewModels;

public sealed class AssistantChatMessageViewModel : ObservableObject
{
    private readonly IUiTextProvider _text;
    private string _content;
    private bool _isRunning;
    private ModelTokenUsage? _modelUsage;
    private string? _modelName;
    private AssistantTaskStatusViewModel? _taskStatus;

    public AssistantChatMessageViewModel(
        ModelMessageRole role,
        string content,
        IUiTextProvider? text = null)
        : this(role, content, isRunning: false, text ?? FallbackUiTextProvider.Instance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
    }

    private AssistantChatMessageViewModel(
        ModelMessageRole role,
        string content,
        bool isRunning,
        IUiTextProvider text)
    {
        if (role is not (ModelMessageRole.User or ModelMessageRole.Assistant))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        _text = text;
        Role = role;
        _content = content;
        _isRunning = isRunning;
        RoleLabel = role == ModelMessageRole.User
            ? text.GetText("AssistantRoleUser", "You")
            : text.GetText("AssistantRoleModel", "Assistant");
    }

    public static AssistantChatMessageViewModel CreatePending(IUiTextProvider text) =>
        new(ModelMessageRole.Assistant, string.Empty, isRunning: true, text);

    public ModelMessageRole Role { get; }

    public string RoleLabel { get; }

    public string Content
    {
        get => _content;
        private set
        {
            if (SetProperty(ref _content, value))
            {
                OnPropertyChanged(nameof(HasContent));
            }
        }
    }

    public bool IsUser => Role == ModelMessageRole.User;

    public bool HasContent => !string.IsNullOrWhiteSpace(Content);

    public ObservableCollection<AssistantActivityViewModel> Activities { get; } = [];

    public bool HasActivities => Activities.Count > 0;

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public ModelTokenUsage? ModelUsage
    {
        get => _modelUsage;
        private set
        {
            if (SetProperty(ref _modelUsage, value))
            {
                OnPropertyChanged(nameof(HasUsage));
                OnPropertyChanged(nameof(UsageSummary));
            }
        }
    }

    public string? ModelName
    {
        get => _modelName;
        private set
        {
            if (SetProperty(ref _modelName, value))
            {
                OnPropertyChanged(nameof(UsageSummary));
            }
        }
    }

    public bool HasUsage => ModelUsage is { ProviderCallCount: > 0 };

    public AssistantTaskStatusViewModel? TaskStatus
    {
        get => _taskStatus;
        private set
        {
            if (SetProperty(ref _taskStatus, value))
            {
                OnPropertyChanged(nameof(HasTaskStatus));
            }
        }
    }

    public bool HasTaskStatus => TaskStatus is not null;

    public string UsageSummary
    {
        get
        {
            if (ModelUsage is not { ProviderCallCount: > 0 } usage)
            {
                return string.Empty;
            }

            string model = string.IsNullOrWhiteSpace(ModelName)
                ? _text.GetText("AssistantUsageUnknownModel", "model")
                : ModelName;
            if (usage.CallsWithUsage == 0)
            {
                return _text.GetText(
                    "AssistantUsageUnavailable",
                    "{0} did not return Token usage for {1} provider call(s).",
                    model,
                    usage.ProviderCallCount);
            }

            string input = usage.InputTokens?.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) ?? "—";
            string output = usage.OutputTokens?.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) ?? "—";
            string total = usage.TotalTokens?.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) ?? "—";
            return usage.IsComplete
                ? _text.GetText(
                    "AssistantUsageComplete",
                    "{0} · input {1} · output {2} · total {3} Tokens · {4} provider call(s)",
                    model,
                    input,
                    output,
                    total,
                    usage.ProviderCallCount)
                : _text.GetText(
                    "AssistantUsagePartial",
                    "{0} returned partial usage · input {1} · output {2} · reported total {3} · {4}/{5} call(s) complete",
                    model,
                    input,
                    output,
                    total,
                    usage.CallsWithCompleteUsage,
                    usage.ProviderCallCount);
        }
    }

    public void ApplyActivity(ModelRunEvent modelEvent)
    {
        ArgumentNullException.ThrowIfNull(modelEvent);
        Activities.Add(new AssistantActivityViewModel(modelEvent, _text));
        OnPropertyChanged(nameof(HasActivities));
    }

    public void Complete(string content, ModelTokenUsage? usage, string? modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        Content = content;
        ModelUsage = usage;
        ModelName = modelName;
        IsRunning = false;
    }

    public void UpdateTaskStatus(ModProjectTaskSnapshot task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (TaskStatus is { } current && current.TaskId == task.TaskId)
        {
            bool currentIsTerminal = !current.IsActive;
            bool incomingIsTerminal = !task.IsActive;
            bool isOlder = current.Revision > 0 && task.Revision > 0
                ? task.Revision < current.Revision
                : task.UpdatedAtUtc < current.UpdatedAtUtc;
            if ((currentIsTerminal && !incomingIsTerminal) ||
                (currentIsTerminal == incomingIsTerminal && isOlder))
            {
                return;
            }
        }

        TaskStatus = new AssistantTaskStatusViewModel(task, _text);
    }
}

public sealed class AssistantTaskStatusViewModel
{
    public AssistantTaskStatusViewModel(ModProjectTaskSnapshot task, IUiTextProvider text)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(text);
        TaskId = task.TaskId;
        Status = task.Status;
        Stage = task.Stage;
        Progress = Math.Clamp(task.Progress, 0, 1);
        UpdatedAtUtc = task.UpdatedAtUtc;
        Revision = task.Revision;
        Title = text.GetText("AssistantToolTaskStatus", "Task status");
        Objective = task.Objective;
        TaskIdText = $"{Title} · {task.TaskId:D}";
        ConfigurationSummary = CreateConfigurationSummary(task, text);
        string status = StatusLabel(task, text);
        Summary = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            "{0} · {1:P0}",
            status,
            Progress);
    }

    public Guid TaskId { get; }

    public ModProjectTaskStatus Status { get; }

    public PipelineStage Stage { get; }

    public double Progress { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public long Revision { get; }

    public string Title { get; }

    public string Objective { get; }

    public string TaskIdText { get; }

    public string ConfigurationSummary { get; }

    public string Summary { get; }

    public bool IsActive => Status is
        ModProjectTaskStatus.Registered or
        ModProjectTaskStatus.Queued or
        ModProjectTaskStatus.Running or
        ModProjectTaskStatus.CancellationRequested;

    internal static string CreateConfigurationSummary(
        ModProjectTaskSnapshot task,
        IUiTextProvider text)
    {
        string sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(task.SourcePath));
        string targetLanguage = TargetLanguageDisplay.GetName(
            text,
            TranslationLanguageCatalog.GetRequired(task.TargetLanguage));
        string style = task.Style switch
        {
            TranslationStyle.Formal => text.GetText("QueueStyleFormal", "Formal translation"),
            TranslationStyle.Informal => text.GetText("QueueStyleInformal", "Tone translation"),
            _ => task.Style.ToString()
        };
        return $"{sourceName} · {targetLanguage} ({task.TargetLanguage}) · {style}";
    }

    private static string StatusLabel(ModProjectTaskSnapshot task, IUiTextProvider text) => task.Status switch
    {
        ModProjectTaskStatus.Registered or ModProjectTaskStatus.Queued =>
            text.GetText("QueueStatusQueued", "Queued"),
        ModProjectTaskStatus.Running => StageLabel(task.Stage, text),
        ModProjectTaskStatus.CancellationRequested => text.GetText(
            "QueueStatusCancellationRequested",
            "Cancellation requested. Waiting for a safe rollback point…"),
        ModProjectTaskStatus.Completed => text.GetText("QueueStatusCompleted", "Completed"),
        ModProjectTaskStatus.Failed => text.GetText(
            "QueueStatusFailed",
            "Failed — review details and retry."),
        ModProjectTaskStatus.Cancelled => text.GetText("QueueStatusCancelled", "Cancelled."),
        _ => task.Status.ToString()
    };

    private static string StageLabel(PipelineStage stage, IUiTextProvider text) => stage switch
    {
        PipelineStage.Queued => text.GetText("QueueProgressQueued", "Queued"),
        PipelineStage.Inspecting => text.GetText("QueueProgressInspecting", "Inspecting package metadata…"),
        PipelineStage.Extracting => text.GetText("QueueProgressExtracting", "Creating a safe workspace…"),
        PipelineStage.Analyzing => text.GetText("QueueProgressAnalyzing", "Analyzing translatable content…"),
        PipelineStage.Translating => text.GetText("QueueProgressTranslating", "Translating changed entries…"),
        PipelineStage.Writing => text.GetText("QueueProgressWriting", "Writing localized resources…"),
        PipelineStage.Repacking => text.GetText("QueueProgressRepacking", "Rebuilding package artifacts…"),
        PipelineStage.Verifying => text.GetText("QueueProgressVerifying", "Verifying package integrity…"),
        PipelineStage.Committing => text.GetText("QueueProgressCommitting", "Committing verified output…"),
        PipelineStage.RollingBack => text.GetText("QueueProgressRollingBack", "Rolling back safely…"),
        PipelineStage.Completed => text.GetText("QueueStatusCompleted", "Completed"),
        PipelineStage.Failed => text.GetText("QueueStatusFailed", "Failed — review details and retry."),
        PipelineStage.Cancelled => text.GetText("QueueStatusCancelled", "Cancelled."),
        _ => text.GetText("QueueProgressWorking", "Working…")
    };
}

public sealed class AssistantActivityViewModel
{
    public AssistantActivityViewModel(ModelRunEvent modelEvent, IUiTextProvider text)
    {
        ArgumentNullException.ThrowIfNull(modelEvent);
        ArgumentNullException.ThrowIfNull(text);
        Sequence = modelEvent.Sequence;
        Kind = modelEvent.Kind;
        IsFailure = modelEvent.Kind is ModelRunEventKind.ToolFailed or ModelRunEventKind.RunFailed;
        Glyph = modelEvent.Kind switch
        {
            ModelRunEventKind.ModelRoundStarted => "\uE895",
            ModelRunEventKind.ModelRoundCompleted => "\uE73E",
            ModelRunEventKind.ToolStarted => "\uE90F",
            ModelRunEventKind.ToolCompleted => "\uE930",
            ModelRunEventKind.ToolFailed or ModelRunEventKind.RunFailed => "\uEA39",
            ModelRunEventKind.RunCancelled => "\uE711",
            _ => "\uE73E"
        };
        Title = CreateTitle(modelEvent, text);
        Detail = CreateDetail(modelEvent, text);
    }

    public int Sequence { get; }

    public ModelRunEventKind Kind { get; }

    public string Glyph { get; }

    public string Title { get; }

    public string Detail { get; }

    public bool IsFailure { get; }

    private static string CreateTitle(ModelRunEvent modelEvent, IUiTextProvider text) => modelEvent.Kind switch
    {
        ModelRunEventKind.ModelRoundStarted => text.GetText(
            "AssistantActivityModelStarted",
            "Requesting model · round {0}",
            modelEvent.Round),
        ModelRunEventKind.ModelRoundCompleted => text.GetText(
            "AssistantActivityModelCompleted",
            "Model round {0} returned",
            modelEvent.Round),
        ModelRunEventKind.ToolStarted => text.GetText(
            "AssistantActivityToolStarted",
            "Running {0}",
            ToolLabel(modelEvent.ToolName, text)),
        ModelRunEventKind.ToolCompleted => text.GetText(
            "AssistantActivityToolCompleted",
            "{0} completed",
            ToolLabel(modelEvent.ToolName, text)),
        ModelRunEventKind.ToolFailed => text.GetText(
            "AssistantActivityToolFailed",
            "{0} was rejected or failed",
            ToolLabel(modelEvent.ToolName, text)),
        ModelRunEventKind.RunCancelled => text.GetText("AssistantActivityCancelled", "Request cancelled"),
        ModelRunEventKind.RunFailed => text.GetText("AssistantActivityFailed", "Request failed"),
        _ => text.GetText("AssistantActivityComplete", "Response completed")
    };

    private static string CreateDetail(ModelRunEvent modelEvent, IUiTextProvider text)
    {
        if (modelEvent.Kind != ModelRunEventKind.ModelRoundCompleted || modelEvent.Usage is null)
        {
            return modelEvent.Kind switch
            {
                ModelRunEventKind.ToolStarted => text.GetText(
                    "AssistantActivityToolSafeDetail",
                    "Arguments and raw results stay hidden; only the public tool state is shown."),
                ModelRunEventKind.RunCompleted => text.GetText(
                    "AssistantActivityCompleteDetail",
                    "The final response and provider-reported usage are ready."),
                _ => string.Empty
            };
        }

        return modelEvent.Usage.CallsWithUsage == 0
            ? text.GetText("AssistantActivityUsageMissing", "This provider round did not return usage.")
            : text.GetText(
                "AssistantActivityUsageReported",
                "Provider usage received for this round.");
    }

    private static string ToolLabel(string? toolName, IUiTextProvider text) => toolName switch
    {
        "system_context" => text.GetText("AssistantToolSystemContext", "safe system context"),
        "cli_propose" => text.GetText("AssistantToolCliProposal", "command proposal validation"),
        "project_get_active" => text.GetText("AssistantToolProjectContext", "active project context"),
        "archive_inspect" => text.GetText("AssistantToolArchiveInspect", "archive inspection"),
        "translation_start" => text.GetText("AssistantToolTranslationStart", "transactional translation"),
        "task_status" => text.GetText("AssistantToolTaskStatus", "task status"),
        "task_cancel" => text.GetText("AssistantToolTaskCancel", "task cancellation"),
        _ => text.GetText("AssistantToolUnknown", "approved tool")
    };
}

public sealed class AssistantProjectOptionViewModel
{
    public AssistantProjectOptionViewModel(ModProjectSnapshot? project, IUiTextProvider text)
    {
        Project = project;
        ProjectId = project?.ProjectId;
        if (project is null)
        {
            DisplayName = text.GetText("AssistantGeneralProject", "General assistant");
            DetailText = text.GetText(
                "AssistantGeneralProjectDetail",
                "No package context; project tools are unavailable.");
            return;
        }

        string sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(project.SourceArtifactPath));
        DisplayName = string.IsNullOrWhiteSpace(project.ModId) ? sourceName : project.ModId;
        ModProjectTaskSnapshot? task = project.ActiveTask ?? project.LatestTask;
        DetailText = task is null
            ? text.GetText("AssistantProjectReady", "{0} · ready", project.Loader ?? "unknown loader")
            : text.GetText(
                "AssistantProjectTaskDetail",
                "{0} · {1} · {2:P0}",
                project.Loader ?? "unknown loader",
                StageLabel(task.Stage, text),
                task.Progress);
        Objective = task?.Objective ?? text.GetText(
            "AssistantProjectNoObjective",
            "No translation task has been created for this project yet.");
        TaskStatus = task?.Status.ToString() ?? text.GetText("AssistantProjectIdle", "Idle");
        Progress = task?.Progress ?? 0;
        ConfigurationSummary = task is null
            ? string.Empty
            : AssistantTaskStatusViewModel.CreateConfigurationSummary(task, text);
        ModelUsageSummary = FormatModelUsage(task, text);
    }

    public Guid? ProjectId { get; }

    public string SelectionId => ProjectId?.ToString("D") ?? "general";

    public ModProjectSnapshot? Project { get; }

    public string DisplayName { get; }

    public string DetailText { get; }

    public string Objective { get; } = string.Empty;

    public string TaskStatus { get; } = string.Empty;

    public double Progress { get; }

    public string ConfigurationSummary { get; } = string.Empty;

    public bool HasProject => Project is not null;

    public string ModelUsageSummary { get; } = string.Empty;

    public bool HasModelUsage => !string.IsNullOrWhiteSpace(ModelUsageSummary);

    private static string FormatModelUsage(ModProjectTaskSnapshot? task, IUiTextProvider text)
    {
        if (task is null)
        {
            return string.Empty;
        }

        ModelTokenUsage? usage = task.ModelUsage;
        if (usage is null)
        {
            return task.Status is
                ModProjectTaskStatus.Completed or
                ModProjectTaskStatus.Failed or
                ModProjectTaskStatus.Cancelled
                    ? text.GetText(
                        "QueueUsageNoCompletedCalls",
                        "Tokens: no provider call completed before the task ended.")
                    : string.Empty;
        }

        if (usage.ProviderCallCount == 0)
        {
            return text.GetText(
                "QueueUsageNoModelCalls",
                "Tokens: no model call was needed; all translations were reused from verified memory.");
        }

        if (usage.CallsWithUsage == 0)
        {
            return text.GetText(
                "QueueUsageUnavailable",
                "Tokens: the provider did not return usage for {0} call(s).",
                usage.ProviderCallCount);
        }

        string input = usage.InputTokens?.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) ?? "—";
        string output = usage.OutputTokens?.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) ?? "—";
        string total = usage.TotalTokens?.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) ?? "—";
        return usage.IsComplete
            ? text.GetText(
                "QueueUsageComplete",
                "Tokens · input {0} · output {1} · total {2} · {3} provider call(s)",
                input,
                output,
                total,
                usage.ProviderCallCount)
            : text.GetText(
                "QueueUsagePartial",
                "Partial Token usage · input {0} · output {1} · reported total {2} · {3}/{4} call(s) complete",
                input,
                output,
                total,
                usage.CallsWithCompleteUsage,
                usage.ProviderCallCount);
    }

    private static string StageLabel(PipelineStage stage, IUiTextProvider text) => stage switch
    {
        PipelineStage.Queued => text.GetText("QueueProgressQueued", "Queued"),
        PipelineStage.Inspecting => text.GetText("QueueProgressInspecting", "Inspecting package metadata…"),
        PipelineStage.Extracting => text.GetText("QueueProgressExtracting", "Creating a safe workspace…"),
        PipelineStage.Analyzing => text.GetText("QueueProgressAnalyzing", "Analyzing translatable content…"),
        PipelineStage.Translating => text.GetText("QueueProgressTranslating", "Translating changed entries…"),
        PipelineStage.Writing => text.GetText("QueueProgressWriting", "Writing localized resources…"),
        PipelineStage.Repacking => text.GetText("QueueProgressRepacking", "Rebuilding package artifacts…"),
        PipelineStage.Verifying => text.GetText("QueueProgressVerifying", "Verifying package integrity…"),
        PipelineStage.Committing => text.GetText("QueueProgressCommitting", "Committing verified output…"),
        PipelineStage.RollingBack => text.GetText("QueueProgressRollingBack", "Rolling back safely…"),
        PipelineStage.Completed => text.GetText("QueueStatusCompleted", "Completed"),
        PipelineStage.Failed => text.GetText("QueueStatusFailed", "Failed"),
        PipelineStage.Cancelled => text.GetText("QueueStatusCancelled", "Cancelled"),
        _ => text.GetText("QueueProgressWorking", "Working…")
    };
}

public sealed class AssistantViewModel : ViewModelBase, IDisposable
{
    private readonly IModelAssistantService _assistantService;
    private readonly IModelSelectionService _selectionService;
    private readonly IUiTextProvider _text;
    private readonly IModProjectWorkspace? _projectWorkspace;
    private readonly IUiDispatcher? _dispatcher;
    private readonly Dictionary<AssistantSessionKey, AssistantConversationSession> _sessions = [];
    private ModelSourceOptionViewModel? _selectedModelSource;
    private AssistantProjectOptionViewModel? _selectedProject;
    private AssistantSessionKey? _activeSessionKey;
    private string _draft = string.Empty;
    private CancellationTokenSource? _sendCancellation;
    private bool _isActivatingSession;
    private bool _isRefreshingModelSources;
    private bool _isRefreshingProjects;
    private bool _allowProjectChanges;
    private bool _disposed;

    public AssistantViewModel(
        IModelAssistantService assistantService,
        IModelSelectionService selectionService,
        IUiTextProvider? text = null,
        IModProjectWorkspace? projectWorkspace = null,
        IUiDispatcher? dispatcher = null)
    {
        _assistantService = assistantService ?? throw new ArgumentNullException(nameof(assistantService));
        _selectionService = selectionService ?? throw new ArgumentNullException(nameof(selectionService));
        _text = text ?? FallbackUiTextProvider.Instance;
        _projectWorkspace = projectWorkspace;
        _dispatcher = dispatcher;
        SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ClearCommand = new RelayCommand(Clear, () => !IsBusy && Messages.Count > 0);
        if (_projectWorkspace is not null)
        {
            _projectWorkspace.Changed += OnProjectWorkspaceChanged;
        }
    }

    public event EventHandler<CliProposalsRequestedEventArgs>? CliProposalsRequested;

    public ObservableCollection<ModelSourceOptionViewModel> ModelSources { get; } = [];

    public ObservableCollection<AssistantProjectOptionViewModel> Projects { get; } = [];

    public ObservableCollection<AssistantChatMessageViewModel> Messages { get; } = [];

    public IAsyncRelayCommand SendCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand ClearCommand { get; }

    public ModelSourceOptionViewModel? SelectedModelSource
    {
        get => _selectedModelSource;
        set
        {
            string? previousSourceId = _selectedModelSource?.Id;
            if (SetProperty(ref _selectedModelSource, value))
            {
                if (previousSourceId is not null &&
                    !string.Equals(previousSourceId, value?.Id, StringComparison.Ordinal))
                {
                    _sendCancellation?.Cancel();
                    AllowProjectChanges = false;
                    ErrorMessage = null;
                    StatusMessage = Text(
                        "AssistantModelChangedConversationPreserved",
                        "Model source changed. Each provider keeps an independent conversation; the previous context was preserved and was not disclosed.");
                }

                OnPropertyChanged(nameof(SelectedModelSourceId));
                ActivateSelectedSession();
                OnPropertyChanged(nameof(HasModelSource));
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasModelSource => SelectedModelSource is not null;

    public string? SelectedModelSourceId
    {
        get => SelectedModelSource?.Id;
        set
        {
            if (_isRefreshingModelSources || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            ModelSourceOptionViewModel? source = ModelSources.FirstOrDefault(option =>
                string.Equals(option.Id, value, StringComparison.Ordinal));
            if (source is not null)
            {
                SelectedModelSource = source;
            }
        }
    }

    public AssistantProjectOptionViewModel? SelectedProject
    {
        get => _selectedProject;
        set
        {
            Guid? previousProjectId = _selectedProject?.ProjectId;
            if (!SetProperty(ref _selectedProject, value))
            {
                return;
            }

            if (value?.ProjectId is { } projectId &&
                _projectWorkspace?.ActiveProject?.ProjectId != projectId)
            {
                _projectWorkspace?.TrySetActiveProject(projectId, out _);
            }

            if (previousProjectId != value?.ProjectId)
            {
                _sendCancellation?.Cancel();
                AllowProjectChanges = false;
                ErrorMessage = null;
                StatusMessage = value?.ProjectId is null
                    ? Text(
                        "AssistantGeneralContextSelected",
                        "General assistant selected. Mod project tools are unavailable in this conversation.")
                    : Text(
                        "AssistantProjectContextSelected",
                        "Project context changed. Its conversation was restored without mixing another mod's history.");
            }

            OnPropertyChanged(nameof(SelectedProjectSelectionId));
            ActivateSelectedSession();
            NotifyProjectProperties();
        }
    }

    public string? SelectedProjectSelectionId
    {
        get => SelectedProject?.SelectionId;
        set
        {
            if (_isRefreshingProjects || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            AssistantProjectOptionViewModel? project = Projects.FirstOrDefault(option =>
                string.Equals(option.SelectionId, value, StringComparison.Ordinal));
            if (project is not null)
            {
                SelectedProject = project;
            }
        }
    }

    public bool HasSelectedProject => SelectedProject?.HasProject == true;

    public string ActiveProjectTitle => SelectedProject?.DisplayName ?? string.Empty;

    public string ActiveProjectSummary => SelectedProject?.DetailText ?? string.Empty;

    public string ActiveProjectObjective => SelectedProject?.Objective ?? string.Empty;

    public string ActiveProjectConfiguration => SelectedProject?.ConfigurationSummary ?? string.Empty;

    public double ActiveProjectProgress => SelectedProject?.Progress ?? 0;

    public string ActiveProjectUsageSummary => SelectedProject?.ModelUsageSummary ?? string.Empty;

    public bool HasActiveProjectUsage => SelectedProject?.HasModelUsage == true;

    public bool AllowProjectChanges
    {
        get => _allowProjectChanges;
        set => SetProperty(ref _allowProjectChanges, value);
    }

    public string Draft
    {
        get => _draft;
        set
        {
            if (SetProperty(ref _draft, value))
            {
                if (!_isActivatingSession && TryGetActiveSession(out AssistantConversationSession? session))
                {
                    session.Draft = value;
                }

                OnPropertyChanged(nameof(DraftLength));
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public int DraftLength => Draft.Length;

    public bool IsConversationEmpty => Messages.Count == 0;

    public void RefreshModelSources()
    {
        ThrowIfDisposed();
        string? selectedId = SelectedModelSource?.Id ?? _selectionService.SelectedSource?.Id;
        _isRefreshingModelSources = true;
        try
        {
            ModelSources.Clear();
            foreach (ModelSource source in _selectionService.Sources)
            {
                ModelSources.Add(new ModelSourceOptionViewModel(source));
            }

            SelectedModelSource = ModelSources.FirstOrDefault(source => source.Id == selectedId)
                ?? ModelSources.FirstOrDefault();
        }
        finally
        {
            _isRefreshingModelSources = false;
        }

        OnPropertyChanged(nameof(SelectedModelSourceId));
        ErrorMessage = ModelSources.Count == 0
            ? Text("AssistantNoModel", "Configure a model source before using the assistant.")
            : null;
        ActivateSelectedSession();
    }

    public void RefreshProjects(Guid? preferredProjectId = null)
    {
        ThrowIfDisposed();
        bool keepGeneralSelection = preferredProjectId is null && SelectedProject is { ProjectId: null };
        Guid? selectedProjectId = preferredProjectId ?? (SelectedProject is null
            ? _projectWorkspace?.ActiveProject?.ProjectId
            : SelectedProject.ProjectId);
        var options = new List<AssistantProjectOptionViewModel>
        {
            new(null, _text)
        };
        if (_projectWorkspace is not null)
        {
            options.AddRange(_projectWorkspace.Projects
                .OrderByDescending(static project => project.UpdatedAtUtc)
                .Select(project => new AssistantProjectOptionViewModel(project, _text)));
        }

        _isRefreshingProjects = true;
        try
        {
            Projects.Clear();
            foreach (AssistantProjectOptionViewModel option in options)
            {
                Projects.Add(option);
            }

            SelectedProject = !keepGeneralSelection && selectedProjectId is { } projectId
                ? Projects.FirstOrDefault(option => option.ProjectId == projectId) ?? Projects[0]
                : Projects[0];
        }
        finally
        {
            _isRefreshingProjects = false;
        }

        OnPropertyChanged(nameof(SelectedProjectSelectionId));
    }

    public void ReportCliProposalReviewFailure()
    {
        ThrowIfDisposed();
        ErrorMessage = Text(
            "AssistantCliReviewFailed",
            "The command proposal could not be opened for review. Nothing was executed.");
    }

    public void PublishPendingCliProposals()
    {
        ThrowIfDisposed();
        EventHandler<CliProposalsRequestedEventArgs>? handlers = CliProposalsRequested;
        if (handlers is null ||
            !TryGetActiveSession(out AssistantConversationSession session) ||
            session.PendingCliProposals.Count == 0)
        {
            return;
        }

        CliCommand[] commands = session.PendingCliProposals.ToArray();
        session.PendingCliProposals.Clear();
        handlers.Invoke(this, new CliProposalsRequestedEventArgs(commands));
    }

    private bool CanSend() =>
        !IsBusy &&
        SelectedModelSource is not null &&
        !string.IsNullOrWhiteSpace(Draft);

    private async Task SendAsync()
    {
        ThrowIfDisposed();
        if (!CanSend() ||
            SelectedModelSource is null ||
            _activeSessionKey is not { } sessionKey ||
            !TryGetActiveSession(out AssistantConversationSession session))
        {
            return;
        }

        string text = Draft;
        string sourceId = SelectedModelSource.Id;
        ModProjectSnapshot? project = SelectedProject?.Project;
        bool allowProjectChanges = project is not null && AllowProjectChanges;
        AllowProjectChanges = false;
        var userMessage = new ModelMessage(ModelMessageRole.User, text);
        var userItem = new AssistantChatMessageViewModel(ModelMessageRole.User, text, _text);
        var pendingItem = AssistantChatMessageViewModel.CreatePending(_text);
        session.Conversation.Add(userMessage);
        session.Messages.Add(userItem);
        session.Messages.Add(pendingItem);
        RenderActiveSessionIf(sessionKey);
        Draft = string.Empty;
        ErrorMessage = null;
        StatusMessage = Text("AssistantThinking", "The model is working…");
        IsBusy = true;
        NotifyCommandStates();
        using var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;
        string? startedTaskIdText = null;
        var progress = new CallbackProgress<ModelRunEvent>(modelEvent =>
        {
            Guid? startedTaskId = null;
            if (modelEvent.Kind == ModelRunEventKind.ToolCompleted &&
                string.Equals(modelEvent.ToolName, "translation_start", StringComparison.Ordinal) &&
                modelEvent.TaskId is { } taskId)
            {
                startedTaskId = taskId;
                Interlocked.CompareExchange(
                    ref startedTaskIdText,
                    taskId.ToString("D"),
                    comparand: null);
            }

            PostToUi(() =>
            {
                pendingItem.ApplyActivity(modelEvent);
                if (startedTaskId is { } publicTaskId)
                {
                    TryAttachStartedTask(
                        session,
                        pendingItem,
                        project?.ProjectId,
                        publicTaskId);
                }
            });
        });
        try
        {
            ModelAssistantCompletion completion = await _assistantService
                .CompleteAsync(
                    sourceId,
                    session.Conversation.ToArray(),
                    project,
                    progress,
                    allowProjectChanges,
                    cancellation.Token)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(completion.Content))
            {
                throw new InvalidDataException("The model returned an empty assistant response.");
            }

            var assistantMessage = new ModelMessage(ModelMessageRole.Assistant, completion.Content);
            session.Conversation.Add(assistantMessage);
            pendingItem.Complete(completion.Content, completion.ModelUsage, completion.Model);
            TryAttachCapturedTask(
                session,
                pendingItem,
                project?.ProjectId,
                Volatile.Read(ref startedTaskIdText));
            RenderActiveSessionIf(sessionKey);
            if (IsActiveSession(sessionKey))
            {
                StatusMessage = completion.ProposedCommands.Count == 0
                    ? Text("AssistantComplete", "Response complete.")
                    : Text(
                        "AssistantProposalReviewRequired",
                        "Response complete. {0} command proposal(s) require separate review.",
                        completion.ProposedCommands.Count);
                if (completion.ProposedCommands.Count > 0)
                {
                    session.PendingCliProposals.AddRange(completion.ProposedCommands);
                    PublishPendingCliProposals();
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            PreserveStartedTaskOrRollBack(
                sessionKey,
                session,
                userMessage,
                userItem,
                pendingItem,
                text,
                project?.ProjectId,
                Volatile.Read(ref startedTaskIdText),
                Text("AssistantCancelled", "The assistant request was cancelled."));
            if (IsActiveSession(sessionKey))
            {
                StatusMessage = Text("AssistantCancelled", "The assistant request was cancelled.");
            }
        }
        catch (ModelServiceException exception)
        {
            PreserveStartedTaskOrRollBack(
                sessionKey,
                session,
                userMessage,
                userItem,
                pendingItem,
                text,
                project?.ProjectId,
                Volatile.Read(ref startedTaskIdText),
                Text(
                    "AssistantRequestFailed",
                    "The assistant request failed. Check the selected model source and try again."));
            if (IsActiveSession(sessionKey))
            {
                ErrorMessage = Text(
                    "AssistantRequestFailedWithDetails",
                    "The assistant request failed: {0}",
                    exception.Message);
                StatusMessage = null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PreserveStartedTaskOrRollBack(
                sessionKey,
                session,
                userMessage,
                userItem,
                pendingItem,
                text,
                project?.ProjectId,
                Volatile.Read(ref startedTaskIdText),
                Text(
                    "AssistantRequestFailed",
                    "The assistant request failed. Check the selected model source and try again."));
            if (IsActiveSession(sessionKey))
            {
                ErrorMessage = Text(
                    "AssistantRequestFailed",
                    "The assistant request failed. Check the selected model source and try again.");
                StatusMessage = null;
            }
        }
        finally
        {
            if (ReferenceEquals(_sendCancellation, cancellation))
            {
                _sendCancellation = null;
            }

            IsBusy = false;
            NotifyCommandStates();
        }
    }

    private void Cancel() => _sendCancellation?.Cancel();

    private void Clear()
    {
        if (IsBusy)
        {
            return;
        }

        if (TryGetActiveSession(out AssistantConversationSession session))
        {
            session.Conversation.Clear();
            session.Messages.Clear();
            session.TaskMessages.Clear();
            session.Draft = string.Empty;
        }

        Messages.Clear();
        Draft = string.Empty;
        OnPropertyChanged(nameof(IsConversationEmpty));
        ErrorMessage = null;
        StatusMessage = Text("AssistantCleared", "Conversation cleared.");
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        SendCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    private void ActivateSelectedSession()
    {
        if (_activeSessionKey is { } previousKey &&
            _sessions.TryGetValue(previousKey, out AssistantConversationSession? previousSession))
        {
            previousSession.Draft = Draft;
        }

        AssistantSessionKey? nextKey = SelectedModelSource is null
            ? null
            : new AssistantSessionKey(SelectedModelSource.Id, SelectedProject?.ProjectId);
        if (_activeSessionKey == nextKey)
        {
            return;
        }

        _activeSessionKey = nextKey;
        Messages.Clear();
        string nextDraft = string.Empty;
        if (nextKey is { } key)
        {
            AssistantConversationSession session = GetOrCreateSession(key);
            foreach (AssistantChatMessageViewModel message in session.Messages)
            {
                Messages.Add(message);
            }

            nextDraft = session.Draft;
        }

        _isActivatingSession = true;
        try
        {
            Draft = nextDraft;
        }
        finally
        {
            _isActivatingSession = false;
        }

        OnPropertyChanged(nameof(IsConversationEmpty));
        NotifyCommandStates();
        PublishPendingCliProposals();
    }

    private void RollBackPendingTurn(
        AssistantSessionKey sessionKey,
        AssistantConversationSession session,
        ModelMessage userMessage,
        AssistantChatMessageViewModel userItem,
        AssistantChatMessageViewModel pendingItem,
        string originalTextForDraft)
    {
        if (session.Conversation.Count > 0 && ReferenceEquals(session.Conversation[^1], userMessage))
        {
            session.Conversation.RemoveAt(session.Conversation.Count - 1);
        }

        session.Messages.Remove(pendingItem);
        session.Messages.Remove(userItem);
        if (string.IsNullOrWhiteSpace(session.Draft))
        {
            session.Draft = originalTextForDraft;
        }

        if (IsActiveSession(sessionKey))
        {
            RenderActiveSessionIf(sessionKey);
            if (string.IsNullOrWhiteSpace(Draft))
            {
                Draft = originalTextForDraft;
            }
        }
    }

    private AssistantConversationSession GetOrCreateSession(AssistantSessionKey key)
    {
        if (!_sessions.TryGetValue(key, out AssistantConversationSession? session))
        {
            session = new AssistantConversationSession();
            _sessions.Add(key, session);
        }

        return session;
    }

    private bool TryGetActiveSession(out AssistantConversationSession session)
    {
        if (_activeSessionKey is { } key)
        {
            session = GetOrCreateSession(key);
            return true;
        }

        session = null!;
        return false;
    }

    private bool IsActiveSession(AssistantSessionKey key) => _activeSessionKey == key;

    private void RenderActiveSessionIf(AssistantSessionKey key)
    {
        if (!IsActiveSession(key) || !_sessions.TryGetValue(key, out AssistantConversationSession? session))
        {
            return;
        }

        Messages.Clear();
        foreach (AssistantChatMessageViewModel message in session.Messages)
        {
            Messages.Add(message);
        }

        OnPropertyChanged(nameof(IsConversationEmpty));
    }

    private void OnProjectWorkspaceChanged(object? sender, ModProjectWorkspaceChangedEventArgs args) =>
        PostToUi(() =>
        {
            if (!_disposed)
            {
                UpdateAttachedTaskStatus(args);
                Guid? preferredProjectId = args.Kind is
                    ModProjectWorkspaceChangeKind.ProjectRegistered or
                    ModProjectWorkspaceChangeKind.ActiveProjectChanged or
                    ModProjectWorkspaceChangeKind.TaskRegistered
                        ? args.Project.ProjectId
                        : null;
                RefreshProjects(preferredProjectId);
            }
        });

    private bool TryAttachCapturedTask(
        AssistantConversationSession session,
        AssistantChatMessageViewModel message,
        Guid? projectId,
        string? taskIdText)
    {
        return Guid.TryParse(taskIdText, out Guid taskId) &&
            TryAttachStartedTask(session, message, projectId, taskId);
    }

    private bool TryAttachStartedTask(
        AssistantConversationSession session,
        AssistantChatMessageViewModel message,
        Guid? projectId,
        Guid taskId)
    {
        if (projectId is not { } expectedProjectId ||
            !session.Messages.Contains(message) ||
            (message.TaskStatus is { } existingTask && existingTask.TaskId != taskId) ||
            _projectWorkspace?.TryGetTask(taskId, out ModProjectTaskSnapshot? task) != true ||
            task is null ||
            task.ProjectId != expectedProjectId)
        {
            return false;
        }

        session.TaskMessages[task.TaskId] = message;
        message.UpdateTaskStatus(task);
        return true;
    }

    private void PreserveStartedTaskOrRollBack(
        AssistantSessionKey sessionKey,
        AssistantConversationSession session,
        ModelMessage userMessage,
        AssistantChatMessageViewModel userItem,
        AssistantChatMessageViewModel pendingItem,
        string originalTextForDraft,
        Guid? projectId,
        string? taskIdText,
        string interruptedResponse)
    {
        bool taskAttached = TryAttachCapturedTask(
                session,
                pendingItem,
                projectId,
                taskIdText) ||
            pendingItem.HasTaskStatus;
        if (!taskAttached)
        {
            RollBackPendingTurn(
                sessionKey,
                session,
                userMessage,
                userItem,
                pendingItem,
                originalTextForDraft);
            return;
        }

        if (session.Conversation.Count > 0 && ReferenceEquals(session.Conversation[^1], userMessage))
        {
            session.Conversation.RemoveAt(session.Conversation.Count - 1);
        }

        pendingItem.Complete(interruptedResponse, usage: null, modelName: null);
        RenderActiveSessionIf(sessionKey);
    }

    private void UpdateAttachedTaskStatus(ModProjectWorkspaceChangedEventArgs args)
    {
        if (args.Task is not { } task)
        {
            return;
        }

        foreach ((AssistantSessionKey key, AssistantConversationSession session) in _sessions)
        {
            if (key.ProjectId == args.Project.ProjectId &&
                session.TaskMessages.TryGetValue(task.TaskId, out AssistantChatMessageViewModel? message))
            {
                message.UpdateTaskStatus(task);
            }
        }
    }

    private void NotifyProjectProperties()
    {
        OnPropertyChanged(nameof(HasSelectedProject));
        OnPropertyChanged(nameof(ActiveProjectTitle));
        OnPropertyChanged(nameof(ActiveProjectSummary));
        OnPropertyChanged(nameof(ActiveProjectObjective));
        OnPropertyChanged(nameof(ActiveProjectConfiguration));
        OnPropertyChanged(nameof(ActiveProjectProgress));
        OnPropertyChanged(nameof(ActiveProjectUsageSummary));
        OnPropertyChanged(nameof(HasActiveProjectUsage));
    }

    private void PostToUi(Action action)
    {
        if (_dispatcher is null)
        {
            action();
        }
        else
        {
            _dispatcher.Post(action);
        }
    }

    private readonly record struct AssistantSessionKey(string ModelSourceId, Guid? ProjectId);

    private sealed class AssistantConversationSession
    {
        public List<ModelMessage> Conversation { get; } = [];

        public List<AssistantChatMessageViewModel> Messages { get; } = [];

        public Dictionary<Guid, AssistantChatMessageViewModel> TaskMessages { get; } = [];

        public string Draft { get; set; } = string.Empty;

        public List<CliCommand> PendingCliProposals { get; } = [];
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        private readonly Action<T> _callback = callback ?? throw new ArgumentNullException(nameof(callback));

        public void Report(T value) => _callback(value);
    }

    private string Text(string key, string fallback, params object?[] arguments) =>
        _text.GetText(key, fallback, arguments);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _sendCancellation?.Cancel();
        _sendCancellation?.Dispose();
        _sendCancellation = null;
        if (_projectWorkspace is not null)
        {
            _projectWorkspace.Changed -= OnProjectWorkspaceChanged;
        }

        _disposed = true;
    }
}

public sealed class CliProposalsRequestedEventArgs(IReadOnlyList<CliCommand> commands) : EventArgs
{
    public IReadOnlyList<CliCommand> Commands { get; } =
        commands?.ToArray() ?? throw new ArgumentNullException(nameof(commands));
}
