using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocaleSmith.Application;
using LocaleSmith.Application.Models;
using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.Presentation.ViewModels;

public sealed class ModelSourceOptionViewModel(ModelSource source) : ObservableObject
{
    public string Id { get; } = source.Id;

    public string DisplayName { get; } = source.DisplayName;

    public string ModelName { get; } = source.ModelName;

    public string ProviderName { get; } = source.Provider switch
    {
        ModelProviderKind.Ollama => "Ollama",
        ModelProviderKind.OpenAiCompatible => "OpenAI compatible",
        ModelProviderKind.Anthropic => "Anthropic",
        _ => source.Provider.ToString()
    };

    public string AccessibleLabel => $"{DisplayName}, {ProviderName}, {ModelName}, configured";
}

public sealed class TranslationStyleOptionViewModel(
    TranslationStyle style,
    string displayName,
    string description)
{
    public TranslationStyle Style { get; } = style;

    public string DisplayName { get; } = displayName;

    public string Description { get; } = description;
}

public sealed class TargetLanguageOptionViewModel(
    TranslationLanguage language,
    IUiTextProvider text)
{
    public TranslationLanguage Language { get; } = language ?? throw new ArgumentNullException(nameof(language));

    public string CanonicalLocale => Language.CanonicalLocale;

    public string DisplayName { get; } = TargetLanguageDisplay.GetName(
        text ?? throw new ArgumentNullException(nameof(text)),
        language);

    public string DetailText => $"{Language.NativeName} · {CanonicalLocale}";

    public string AccessibleLabel => $"{DisplayName}, {Language.NativeName}, {CanonicalLocale}";
}

internal static class TargetLanguageDisplay
{
    public static string GetName(IUiTextProvider text, TranslationLanguage language) =>
        text.GetText($"TargetLanguage_{language.CanonicalLocale}", language.EnglishName);
}

public sealed class QueueStageDetailViewModel(
    PipelineStage stage,
    PipelineStageStatus status,
    string stageName,
    string statusText,
    string timingText,
    DateTimeOffset? startedAtUtc,
    DateTimeOffset? finishedAtUtc)
{
    public PipelineStage Stage { get; } = stage;

    public PipelineStageStatus Status { get; } = status;

    public string StageName { get; } = stageName;

    public string StatusText { get; } = statusText;

    public string TimingText { get; } = timingText;

    public DateTimeOffset? StartedAtUtc { get; } = startedAtUtc;

    public DateTimeOffset? FinishedAtUtc { get; } = finishedAtUtc;

    public bool HasTiming => !string.IsNullOrWhiteSpace(TimingText);

    public bool IsPending => Status == PipelineStageStatus.Pending;

    public bool IsCurrent => Status == PipelineStageStatus.Current;

    public bool IsCompleted => Status == PipelineStageStatus.Completed;

    public bool IsFailed => Status == PipelineStageStatus.Failed;

    public bool IsCancelled => Status == PipelineStageStatus.Cancelled;

    public bool IsSkipped => Status == PipelineStageStatus.Skipped;
}

public sealed class QueueItemViewModel : ObservableObject
{
    private PipelineStage _stage = PipelineStage.Queued;
    private double _progress;
    private readonly IUiTextProvider _text;
    private string _status;
    private string _modId;
    private string _loader;
    private string? _errorDetails;
    private string? _technicalErrorDetails;
    private bool _artifactReady;
    private IReadOnlyList<HardcodedStringCandidate> _hardcodedCandidates = [];
    private int _externalizedCount;
    private string _nextAction = string.Empty;
    private IReadOnlyList<QueueStageDetailViewModel> _stageDetails = [];
    private PipelineStageStatus? _rollbackStatus;
    private bool _isDetailsExpanded;
    private bool _isCancellationRequested;

    public QueueItemViewModel(
        Guid jobId,
        string sourcePath,
        string outputPath,
        IUiTextProvider? text = null,
        TranslationStyle style = TranslationStyle.Formal,
        string targetLanguage = TranslationLanguageCatalog.DefaultLocale)
    {
        _text = text ?? FallbackUiTextProvider.Instance;
        JobId = jobId;
        SourcePath = sourcePath;
        OutputPath = outputPath;
        Style = style;
        TargetLanguage = TranslationLanguageCatalog.NormalizeLocale(targetLanguage);
        FileName = Directory.Exists(sourcePath)
            ? new DirectoryInfo(sourcePath).Name
            : Path.GetFileName(sourcePath);
        _status = _text.GetText("QueueStatusQueued", "Queued");
        _modId = _text.GetText("QueueStatusDetecting", "Detecting…");
        _loader = _modId;
    }

    public Guid JobId { get; }

    public string SourcePath { get; }

    public string OutputPath { get; }

    public string FileName { get; }

    public string CancelAccessibleName => _text.GetText(
        "QueueCancelAccessibleName",
        "Cancel translation job {0}",
        FileName);

    public TranslationStyle Style { get; }

    public string TargetLanguage { get; }

    public string TargetLanguageName => TargetLanguageDisplay.GetName(
        _text,
        TranslationLanguageCatalog.GetRequired(TargetLanguage));

    public string TranslationStyleName => Style switch
    {
        TranslationStyle.Formal => _text.GetText("QueueStyleFormal", "Formal translation"),
        TranslationStyle.Informal => _text.GetText("QueueStyleInformal", "Tone translation"),
        _ => Style.ToString()
    };

    public string TranslationProfile => _text.GetText(
        "QueueTranslationProfile",
        "{0} · {1}",
        TargetLanguageName,
        TranslationStyleName);

    public PipelineStage Stage
    {
        get => _stage;
        private set
        {
            if (SetProperty(ref _stage, value))
            {
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CurrentAction));
            }
        }
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, Math.Clamp(value, 0, 1));
    }

    public double ProgressPercent => Progress * 100;

    public string ProgressText => FormattableString.Invariant($"{ProgressPercent:0}%");

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>The localized action represented by the backend's current stable pipeline stage.</summary>
    public string CurrentAction => ProgressStatus(Stage);

    /// <summary>The localized action for the next backend-reported stage, or an empty string at a terminal stage.</summary>
    public string NextAction
    {
        get => _nextAction;
        private set
        {
            if (SetProperty(ref _nextAction, value))
            {
                OnPropertyChanged(nameof(HasNextAction));
            }
        }
    }

    public bool HasNextAction => !string.IsNullOrWhiteSpace(NextAction);

    public IReadOnlyList<QueueStageDetailViewModel> StageDetails
    {
        get => _stageDetails;
        private set
        {
            if (SetProperty(ref _stageDetails, value))
            {
                OnPropertyChanged(nameof(HasStageDetails));
                OnPropertyChanged(nameof(StageDetailsSummary));
            }
        }
    }

    public bool HasStageDetails => StageDetails.Count > 0;

    public string StageDetailsSummary => string.Join(
        System.Environment.NewLine,
        StageDetails.Select(static detail => string.IsNullOrWhiteSpace(detail.TimingText)
            ? $"{detail.StageName}: {detail.StatusText}"
            : $"{detail.StageName}: {detail.StatusText} ({detail.TimingText})"));

    public string DetailsEmptyMessage => _text.GetText(
        "QueueProgressDetailsPending",
        "Detailed stage information will appear when processing begins.");

    public PipelineStageStatus? RollbackStatus
    {
        get => _rollbackStatus;
        private set
        {
            if (SetProperty(ref _rollbackStatus, value))
            {
                OnPropertyChanged(nameof(HasRollbackStatus));
                OnPropertyChanged(nameof(RollbackStatusText));
                OnPropertyChanged(nameof(HasFailureDetails));
            }
        }
    }

    public bool HasRollbackStatus => RollbackStatus is not null and not (
        PipelineStageStatus.Pending or PipelineStageStatus.Skipped);

    public string RollbackStatusText => RollbackStatus is { } status
        ? _text.GetText("QueueRollbackStatus", "Rollback: {0}", StageStatusText(status))
        : string.Empty;

    public bool IsDetailsExpanded
    {
        get => _isDetailsExpanded;
        set => SetProperty(ref _isDetailsExpanded, value);
    }

    public bool IsCancellationRequested
    {
        get => _isCancellationRequested;
        private set
        {
            if (SetProperty(ref _isCancellationRequested, value))
            {
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public string ModId
    {
        get => _modId;
        private set => SetProperty(ref _modId, value);
    }

    public string Loader
    {
        get => _loader;
        private set => SetProperty(ref _loader, value);
    }

    public string? ErrorDetails
    {
        get => _errorDetails;
        private set
        {
            if (SetProperty(ref _errorDetails, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(HasFailureDetails));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorDetails);

    /// <summary>
    /// Includes ordinary job failures and the narrower cancellation case where a real rollback failed.
    /// Successful or unnecessary rollback does not create a diagnostic warning panel.
    /// </summary>
    public bool HasFailureDetails => HasError || RollbackStatus == PipelineStageStatus.Failed;

    public string? TechnicalErrorDetails
    {
        get => _technicalErrorDetails;
        private set
        {
            if (SetProperty(ref _technicalErrorDetails, value))
            {
                OnPropertyChanged(nameof(HasTechnicalErrorDetails));
            }
        }
    }

    public bool HasTechnicalErrorDetails => !string.IsNullOrWhiteSpace(TechnicalErrorDetails);

    public bool CanCancel => !IsCancellationRequested && Stage is not (
        PipelineStage.RollingBack or
        PipelineStage.Completed or
        PipelineStage.Failed or
        PipelineStage.Cancelled);

    public bool ArtifactReady
    {
        get => _artifactReady;
        private set
        {
            if (SetProperty(ref _artifactReady, value))
            {
                OnPropertyChanged(nameof(ArtifactStatus));
            }
        }
    }

    public string ArtifactStatus => FormatArtifactStatus(ArtifactReady);

    public IReadOnlyList<HardcodedStringCandidate> HardcodedCandidates
    {
        get => _hardcodedCandidates;
        private set
        {
            if (SetProperty(ref _hardcodedCandidates, value))
            {
                OnPropertyChanged(nameof(HardcodedCandidateCount));
                OnPropertyChanged(nameof(HardcodedSummary));
            }
        }
    }

    public int HardcodedCandidateCount => HardcodedCandidates.Count;

    public string HardcodedSummary => _text.GetText(
        "QueueHardcodedSummary",
        "Hard-coded candidates: {0}; externalized: {1}.",
        HardcodedCandidateCount,
        ExternalizedCount);

    public int ExternalizedCount
    {
        get => _externalizedCount;
        private set
        {
            if (SetProperty(ref _externalizedCount, value))
            {
                OnPropertyChanged(nameof(HardcodedSummary));
            }
        }
    }

    public void Update(TranslationQueueProgress progress)
    {
        Stage = progress.Stage;
        if (progress.Stage is PipelineStage.Completed or PipelineStage.Failed or PipelineStage.Cancelled)
        {
            IsCancellationRequested = false;
        }

        Progress = progress.Fraction;
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        Status = ProgressStatus(progress.Stage);
        NextAction = progress.NextStage is { } nextStage
            ? ProgressStatus(nextStage)
            : string.Empty;
        if (progress.Stages is not null)
        {
            StageDetails = progress.Stages
                .Select(CreateStageDetail)
                .ToArray();
        }

        RollbackStatus = progress.RollbackStatus;
    }

    public void RequestCancellation()
    {
        IsCancellationRequested = true;
        Status = _text.GetText(
            "QueueStatusCancellationRequested",
            "Cancellation requested. Waiting for a safe rollback point…");
    }

    public void Complete(TranslationQueueResult result)
    {
        ModId = result.ModId;
        Loader = result.Loader;
        Stage = PipelineStage.Completed;
        IsCancellationRequested = false;
        NextAction = string.Empty;
        Progress = 1;
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        Status = _text.GetText("QueueStatusCompleted", "Completed");
        ArtifactReady = result.ArtifactPaths.Count == 1;
        HardcodedCandidates = result.HardcodedCandidates.ToArray();
        ExternalizedCount = result.ExternalizedCount;
    }

    public void Fail(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        FailCore(message, guidance: null);
    }

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        FailCore(CreateTechnicalErrorDetails(exception), CreateFailureGuidance(exception));
    }

    private void FailCore(string technicalDetails, string? guidance)
    {
        Stage = PipelineStage.Failed;
        IsCancellationRequested = false;
        NextAction = string.Empty;
        Status = _text.GetText("QueueStatusFailed", "Failed — review details and retry.");
        var rollbackSummary = RollbackStatus switch
        {
            PipelineStageStatus.Completed => _text.GetText(
                "QueueFailureSummaryRolledBack",
                "The job failed, and all staged changes were rolled back safely."),
            PipelineStageStatus.Failed => _text.GetText(
                "QueueFailureSummaryRollbackFailed",
                "The job failed and rollback did not finish. Review the workspace before retrying."),
            _ => _text.GetText(
                "QueueFailureSummaryNoRollback",
                _text.GetText(
                    "QueueFailureSummary",
                    "The job failed before output was committed; no rollback was needed."))
        };
        ErrorDetails = string.IsNullOrWhiteSpace(guidance)
            ? rollbackSummary
            : $"{rollbackSummary} {guidance}";
        TechnicalErrorDetails = technicalDetails;
    }

    private string? CreateFailureGuidance(Exception exception)
    {
        var cause = GetFailureCause(exception);
        return cause switch
        {
            ModelServiceException
            {
                StatusCode: HttpStatusCode.Unauthorized
            } => _text.GetText(
                "QueueFailureModelCredentials",
                "The model service rejected the saved credential. Update the API key in Model sources, test the connection, and retry."),
            ModelServiceException => _text.GetText(
                "QueueFailureModelService",
                "The model service request failed. Review the saved endpoint, model, credential, and connection test before retrying."),
            TranslationContractException => _text.GetText(
                "QueueFailureModelResponse",
                "The model returned a response that could not be applied safely. Review the technical details and retry."),
            _ => null
        };
    }

    private static string CreateTechnicalErrorDetails(Exception exception)
    {
        var pipelineFailure = exception as PipelineException;
        var cause = GetFailureCause(exception);
        var stage = pipelineFailure?.FailedStage.ToString() ?? "none";
        var prefix = $"stage={stage} | cause={cause.GetType().Name}";

        return cause switch
        {
            ModelServiceException modelFailure => string.Create(
                CultureInfo.InvariantCulture,
                $"{prefix} | http={FormatHttpStatus(modelFailure.StatusCode)} | " +
                $"request={modelFailure.RequestId ?? "none"}"),
            TranslationContractException => prefix,
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"{prefix} | hresult=0x{exception.HResult:X8}")
        };
    }

    private static Exception GetFailureCause(Exception exception) =>
        exception is PipelineException { InnerException: { } innerException }
            ? innerException
            : exception;

    private static string FormatHttpStatus(HttpStatusCode? statusCode) =>
        statusCode is { } status
            ? ((int)status).ToString(CultureInfo.InvariantCulture)
            : "none";

    public void Cancelled()
    {
        Stage = PipelineStage.Cancelled;
        IsCancellationRequested = false;
        NextAction = string.Empty;
        Status = RollbackStatus switch
        {
            PipelineStageStatus.Completed => _text.GetText(
                "QueueStatusCancelledRolledBack",
                "Cancelled and rolled back safely."),
            PipelineStageStatus.Failed => _text.GetText(
                "QueueStatusCancelledRollbackFailed",
                "Cancelled, but rollback did not finish. Review the workspace."),
            _ => _text.GetText(
                "QueueStatusCancelledNoRollback",
                "Cancelled before output was committed; no rollback was needed.")
        };
    }

    private string FormatArtifactStatus(bool ready) => ready
        ? _text.GetText("QueueArtifactReady", "Ready")
        : _text.GetText("QueueArtifactPending", "Pending");

    private string ProgressStatus(PipelineStage stage) => stage switch
    {
        PipelineStage.Queued => _text.GetText("QueueProgressQueued", "Queued"),
        PipelineStage.Inspecting => _text.GetText("QueueProgressInspecting", "Inspecting package metadata…"),
        PipelineStage.Extracting => _text.GetText("QueueProgressExtracting", "Creating a safe workspace…"),
        PipelineStage.Analyzing => _text.GetText("QueueProgressAnalyzing", "Analyzing translatable content…"),
        PipelineStage.Translating => _text.GetText("QueueProgressTranslating", "Translating changed entries…"),
        PipelineStage.Writing => _text.GetText("QueueProgressWriting", "Writing localized resources…"),
        PipelineStage.Repacking => _text.GetText("QueueProgressRepacking", "Rebuilding package artifacts…"),
        PipelineStage.Verifying => _text.GetText("QueueProgressVerifying", "Verifying package integrity…"),
        PipelineStage.Committing => _text.GetText("QueueProgressCommitting", "Committing verified output…"),
        PipelineStage.RollingBack => _text.GetText("QueueProgressRollingBack", "Rolling back safely…"),
        PipelineStage.Completed => _text.GetText("QueueStatusCompleted", "Completed"),
        PipelineStage.Failed => _text.GetText("QueueStatusFailed", "Failed — review details and retry."),
        PipelineStage.Cancelled => _text.GetText("QueueStatusCancelled", "Cancelled."),
        _ => _text.GetText("QueueProgressWorking", "Working…")
    };

    private QueueStageDetailViewModel CreateStageDetail(PipelineStageProgress progress) => new(
        progress.Stage,
        progress.Status,
        StageName(progress.Stage),
        StageStatusText(progress.Status),
        FormatTiming(progress.StartedAtUtc, progress.FinishedAtUtc),
        progress.StartedAtUtc,
        progress.FinishedAtUtc);

    private string StageName(PipelineStage stage) => stage switch
    {
        PipelineStage.Queued => _text.GetText("QueueStageQueued", "Queued"),
        PipelineStage.Inspecting => _text.GetText("QueueStageInspecting", "Inspect package"),
        PipelineStage.Extracting => _text.GetText("QueueStageExtracting", "Create safe workspace"),
        PipelineStage.Analyzing => _text.GetText("QueueStageAnalyzing", "Analyze content"),
        PipelineStage.Translating => _text.GetText("QueueStageTranslating", "Translate entries"),
        PipelineStage.Writing => _text.GetText("QueueStageWriting", "Write resources"),
        PipelineStage.Repacking => _text.GetText("QueueStageRepacking", "Rebuild package"),
        PipelineStage.Verifying => _text.GetText("QueueStageVerifying", "Verify integrity"),
        PipelineStage.Committing => _text.GetText("QueueStageCommitting", "Commit output"),
        PipelineStage.RollingBack => _text.GetText("QueueStageRollingBack", "Safe rollback"),
        _ => ProgressStatus(stage)
    };

    private string StageStatusText(PipelineStageStatus status) => status switch
    {
        PipelineStageStatus.Pending => _text.GetText("QueueStageStatusPending", "Pending"),
        PipelineStageStatus.Current => _text.GetText("QueueStageStatusCurrent", "In progress"),
        PipelineStageStatus.Completed => _text.GetText("QueueStageStatusCompleted", "Completed"),
        PipelineStageStatus.Failed => _text.GetText("QueueStageStatusFailed", "Failed"),
        PipelineStageStatus.Cancelled => _text.GetText("QueueStageStatusCancelled", "Cancelled"),
        PipelineStageStatus.Skipped => _text.GetText("QueueStageStatusSkipped", "Not needed"),
        _ => status.ToString()
    };

    private string FormatTiming(DateTimeOffset? startedAtUtc, DateTimeOffset? finishedAtUtc)
    {
        if (startedAtUtc is null)
        {
            return string.Empty;
        }

        var started = startedAtUtc.Value.ToLocalTime();
        if (finishedAtUtc is null)
        {
            return _text.GetText("QueueStageTimingStarted", "Started {0:t}", started);
        }

        var finished = finishedAtUtc.Value.ToLocalTime();
        var duration = finishedAtUtc.Value - startedAtUtc.Value;
        var durationText = duration.TotalMinutes >= 1
            ? string.Format(CultureInfo.CurrentCulture, "{0:0}:{1:00}", Math.Floor(duration.TotalMinutes), duration.Seconds)
            : _text.GetText("QueueStageDurationSeconds", "{0:0.#} s", Math.Max(0, duration.TotalSeconds));
        return _text.GetText(
            "QueueStageTimingCompleted",
            "{0:t}–{1:t} ({2})",
            started,
            finished,
            durationText);
    }
}

public sealed class DashboardViewModel : ViewModelBase
{
    private readonly IModelSelectionService _modelSelectionService;
    private readonly ITranslationQueueService _translationQueueService;
    private readonly IOutputPathStrategy _outputPathStrategy;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUiTextProvider _text;
    private readonly Dictionary<Guid, TranslationQueueHandle> _handles = [];
    private readonly Dictionary<Guid, QueueItemViewModel> _items = [];
    private ModelSourceOptionViewModel? _selectedModelSource;
    private TranslationStyleOptionViewModel _selectedTranslationStyle;
    private TargetLanguageOptionViewModel _selectedTargetLanguage;
    private Task<bool> _modelSelectionTask = Task.FromResult(true);
    private string? _requestedModelSourceId;
    private int _modelSelectionVersion;
    private bool _isApplyingModelSelectionState;
    private bool _isModelSelectionPending;

    public DashboardViewModel(
        IModelSelectionService modelSelectionService,
        ITranslationQueueService translationQueueService,
        IOutputPathStrategy outputPathStrategy,
        IUiDispatcher dispatcher,
        IUiTextProvider? text = null)
    {
        _modelSelectionService = modelSelectionService ?? throw new ArgumentNullException(nameof(modelSelectionService));
        _translationQueueService = translationQueueService ?? throw new ArgumentNullException(nameof(translationQueueService));
        _outputPathStrategy = outputPathStrategy ?? throw new ArgumentNullException(nameof(outputPathStrategy));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _text = text ?? FallbackUiTextProvider.Instance;
        TranslationStyles =
        [
            new TranslationStyleOptionViewModel(
                TranslationStyle.Formal,
                Text("QueueStyleFormal", "Formal translation"),
                Text("QueueStyleFormalDescription", "Official terminology and consistent written language.")),
            new TranslationStyleOptionViewModel(
                TranslationStyle.Informal,
                Text("QueueStyleInformal", "Tone translation"),
                Text("QueueStyleInformalDescription", "Natural player-community wording with an informal tone."))
        ];
        _selectedTranslationStyle = TranslationStyles[0];
        TargetLanguages = TranslationLanguageCatalog.SupportedLanguages
            .Select(language => new TargetLanguageOptionViewModel(language, _text))
            .ToArray();
        _selectedTargetLanguage = TargetLanguages.Single(language =>
            string.Equals(
                language.CanonicalLocale,
                TranslationLanguageCatalog.DefaultLocale,
                StringComparison.Ordinal));
        CancelCommand = new RelayCommand<QueueItemViewModel>(Cancel, static item => item?.CanCancel == true);
        if (_modelSelectionService is IModelSelectionStateNotifier notifier)
        {
            notifier.StateChanged += OnModelSelectionStateChanged;
        }

        RefreshModelSources();
        _translationQueueService.ProgressChanged += OnProgressChanged;
    }

    public ObservableCollection<ModelSourceOptionViewModel> ModelSources { get; } = [];

    public ObservableCollection<QueueItemViewModel> QueueItems { get; } = [];

    public IReadOnlyList<TranslationStyleOptionViewModel> TranslationStyles { get; }

    public IReadOnlyList<TargetLanguageOptionViewModel> TargetLanguages { get; }

    public IRelayCommand<QueueItemViewModel> CancelCommand { get; }

    public bool IsQueueEmpty => QueueItems.Count == 0;

    public bool HasActiveTranslationJobs => _handles.Count != 0;

    public bool HasModelSources => ModelSources.Count > 0;

    public bool HasSelectedModelSource => SelectedModelSource is not null;

    public bool IsModelSelectionPending
    {
        get => _isModelSelectionPending;
        private set
        {
            if (SetProperty(ref _isModelSelectionPending, value))
            {
                OnPropertyChanged(nameof(CanEnqueuePackages));
            }
        }
    }

    public bool CanEnqueuePackages => HasSelectedModelSource && !IsModelSelectionPending;

    public string EmptyModelSourcesMessage => Text(
        "QueueNoModelSources",
        "No model sources are available. Configure one in Model sources before adding packages.");

    public TranslationStyleOptionViewModel SelectedTranslationStyle
    {
        get => _selectedTranslationStyle;
        set => SetProperty(
            ref _selectedTranslationStyle,
            value ?? throw new ArgumentNullException(nameof(value)));
    }

    public TargetLanguageOptionViewModel SelectedTargetLanguage
    {
        get => _selectedTargetLanguage;
        set => SetProperty(
            ref _selectedTargetLanguage,
            value ?? throw new ArgumentNullException(nameof(value)));
    }

    public ModelSourceOptionViewModel? SelectedModelSource
    {
        get => _selectedModelSource;
        set
        {
            if (!SetSelectedModelSource(value) || _isApplyingModelSelectionState)
            {
                return;
            }

            if (value is null)
            {
                RefreshModelSources();
                return;
            }

            BeginModelSelection(value.Id, isFallback: false);
        }
    }

    /// <summary>
    /// Stable identifier used by the ComboBox so replacing option view-model instances during a refresh does not
    /// clear an otherwise valid selection.
    /// </summary>
    public string? SelectedModelSourceId
    {
        get => SelectedModelSource?.Id;
        set
        {
            if (_isApplyingModelSelectionState ||
                string.Equals(value, SelectedModelSource?.Id, StringComparison.Ordinal))
            {
                return;
            }

            var option = value is null
                ? null
                : ModelSources.FirstOrDefault(source => string.Equals(source.Id, value, StringComparison.Ordinal));
            if (option is null)
            {
                ErrorMessage = Text(
                    "QueueModelUnavailable",
                    "The selected model source is no longer available.");
                RefreshModelSources();
                return;
            }

            SelectedModelSource = option;
        }
    }

    public void RefreshModelSources()
    {
        ApplyModelSelectionState(
            _modelSelectionService.Sources,
            _modelSelectionService.SelectedSource,
            announceInvalidPreviousSelection: true,
            selectFallback: true);
    }

    public async Task EnqueuePackagesAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ErrorMessage = null;
        if (!await _modelSelectionTask.WaitAsync(cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        var selectedSource = _modelSelectionService.SelectedSource;
        if (selectedSource is null ||
            !_modelSelectionService.Sources.Any(source =>
                string.Equals(source.Id, selectedSource.Id, StringComparison.Ordinal)))
        {
            ErrorMessage = Text(
                "QueueModelRequired",
                "Configure and select a model source before adding packages.");
            RefreshModelSources();
            return;
        }

        // A multi-package add operation captures one model, target language and style. Later
        // selector changes affect only later operations.
        var modelSourceId = selectedSource.Id;
        var translationStyle = SelectedTranslationStyle.Style;
        var targetLanguage = SelectedTargetLanguage.CanonicalLocale;

        foreach (var path in paths.Where(static path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                ErrorMessage = Text("QueuePackageNotFound", "Package not found: {0}", fullPath);
                continue;
            }

            try
            {
                var outputPath = await _outputPathStrategy
                    .CreateOutputPathAsync(fullPath, targetLanguage, cancellationToken)
                    .ConfigureAwait(true);
                var handle = await _translationQueueService.EnqueueAsync(
                    new TranslationQueueRequest(
                        fullPath,
                        outputPath,
                        modelSourceId,
                        translationStyle,
                        targetLanguage),
                    cancellationToken).ConfigureAwait(true);
                var item = new QueueItemViewModel(
                    handle.JobId,
                    fullPath,
                    outputPath,
                    _text,
                    translationStyle,
                    targetLanguage);
                _handles[handle.JobId] = handle;
                _items[handle.JobId] = item;
                QueueItems.Add(item);
                OnPropertyChanged(nameof(IsQueueEmpty));
                if (handle.LatestProgress is { } latestProgress)
                {
                    item.Update(latestProgress);
                }

                _ = MonitorAsync(handle, item);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ErrorMessage = Text(
                    "QueueEnqueueFailed",
                    "Could not enqueue {0}. Check the output workspace safety settings and retry.",
                    Path.GetFileName(fullPath));
            }
        }
    }

    private void BeginModelSelection(string sourceId, bool isFallback)
    {
        if (IsModelSelectionPending &&
            string.Equals(_requestedModelSourceId, sourceId, StringComparison.Ordinal))
        {
            return;
        }

        _requestedModelSourceId = sourceId;
        var version = ++_modelSelectionVersion;
        IsModelSelectionPending = true;
        _modelSelectionTask = SelectSourceAndReportAsync(sourceId, version, isFallback);
    }

    private async Task<bool> SelectSourceAndReportAsync(string sourceId, int version, bool isFallback)
    {
        // Ensure BeginModelSelection publishes this task before a synchronous test/service implementation completes.
        await Task.Yield();
        try
        {
            if (!await _modelSelectionService.SelectSourceAsync(sourceId).ConfigureAwait(true))
            {
                if (version == _modelSelectionVersion)
                {
                    ApplyModelSelectionState(
                        _modelSelectionService.Sources,
                        _modelSelectionService.SelectedSource,
                        announceInvalidPreviousSelection: false,
                        selectFallback: false);
                    ErrorMessage = Text(
                        "QueueModelUnavailable",
                        "The selected model source is no longer available.");
                }

                return false;
            }

            if (version == _modelSelectionVersion)
            {
                ApplyModelSelectionState(
                    _modelSelectionService.Sources,
                    _modelSelectionService.SelectedSource,
                    announceInvalidPreviousSelection: false,
                    selectFallback: false);
                ErrorMessage = null;
                if (!isFallback)
                {
                    StatusMessage = Text(
                        "QueueModelChanged",
                        "Model source changed. Existing requests keep their original source.");
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (version == _modelSelectionVersion)
            {
                ApplyModelSelectionState(
                    _modelSelectionService.Sources,
                    _modelSelectionService.SelectedSource,
                    announceInvalidPreviousSelection: false,
                    selectFallback: false);
                ErrorMessage = Text(
                    "QueueModelSelectFailed",
                    "Model source could not be selected: {0}",
                    exception.Message);
            }

            return false;
        }
        finally
        {
            if (version == _modelSelectionVersion)
            {
                _requestedModelSourceId = null;
                IsModelSelectionPending = false;
                _modelSelectionTask = Task.FromResult(true);
            }
        }
    }

    private void OnModelSelectionStateChanged(
        object? sender,
        ModelSelectionStateChangedEventArgs args) =>
        _dispatcher.Post(() => ApplyModelSelectionState(
            args.Sources,
            args.SelectedSource,
            announceInvalidPreviousSelection: true,
            selectFallback: true));

    private void ApplyModelSelectionState(
        IReadOnlyList<ModelSource> sources,
        ModelSource? selectedSource,
        bool announceInvalidPreviousSelection,
        bool selectFallback)
    {
        var previousId = SelectedModelSource?.Id;
        var previousStillAvailable = previousId is not null && sources.Any(source =>
            string.Equals(source.Id, previousId, StringComparison.Ordinal));
        var selectedId = selectedSource?.Id;

        _isApplyingModelSelectionState = true;
        try
        {
            ModelSources.Clear();
            foreach (var source in sources)
            {
                ModelSources.Add(new ModelSourceOptionViewModel(source));
            }

            var selected = selectedId is null
                ? null
                : ModelSources.FirstOrDefault(source =>
                    string.Equals(source.Id, selectedId, StringComparison.Ordinal));
            selected ??= previousStillAvailable
                ? ModelSources.First(source => string.Equals(source.Id, previousId, StringComparison.Ordinal))
                : selectFallback ? ModelSources.FirstOrDefault() : null;
            SetSelectedModelSource(selected);
        }
        finally
        {
            _isApplyingModelSelectionState = false;
        }

        OnPropertyChanged(nameof(HasModelSources));
        OnPropertyChanged(nameof(CanEnqueuePackages));

        if (SelectedModelSource is null)
        {
            if (announceInvalidPreviousSelection && previousId is not null)
            {
                StatusMessage = EmptyModelSourcesMessage;
            }

            return;
        }

        var serviceSelectionIsAvailable = selectedId is not null && ModelSources.Any(source =>
            string.Equals(source.Id, selectedId, StringComparison.Ordinal));
        var needsFallbackSelection = !serviceSelectionIsAvailable;
        if (announceInvalidPreviousSelection &&
            ((previousId is not null && !previousStillAvailable) || needsFallbackSelection))
        {
            StatusMessage = Text(
                "QueueModelFallback",
                "The previous model source is unavailable. New requests will use {0} ({1}).",
                SelectedModelSource.DisplayName,
                SelectedModelSource.ModelName);
        }

        if (selectFallback && needsFallbackSelection)
        {
            BeginModelSelection(SelectedModelSource.Id, isFallback: true);
        }
    }

    private bool SetSelectedModelSource(ModelSourceOptionViewModel? value)
    {
        if (!SetProperty(ref _selectedModelSource, value, nameof(SelectedModelSource)))
        {
            return false;
        }

        OnPropertyChanged(nameof(SelectedModelSourceId));
        OnPropertyChanged(nameof(HasSelectedModelSource));
        OnPropertyChanged(nameof(CanEnqueuePackages));
        return true;
    }

    private async Task MonitorAsync(TranslationQueueHandle handle, QueueItemViewModel item)
    {
        try
        {
            var result = await handle.Completion.ConfigureAwait(false);
            _dispatcher.Post(() => item.Complete(result));
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(item.Cancelled);
        }
        catch (Exception exception)
        {
            _dispatcher.Post(() => item.Fail(exception));
        }
        finally
        {
            _dispatcher.Post(() =>
            {
                _handles.Remove(handle.JobId);
                CancelCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private void Cancel(QueueItemViewModel? item)
    {
        if (item is null || !_handles.TryGetValue(item.JobId, out var handle) || !item.CanCancel)
        {
            return;
        }

        handle.Cancel();
        item.RequestCancellation();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void OnProgressChanged(object? sender, TranslationQueueProgress progress)
    {
        _dispatcher.Post(() =>
        {
            if (!_items.TryGetValue(progress.JobId, out var item))
            {
                return;
            }

            item.Update(progress);
            CancelCommand.NotifyCanExecuteChanged();
        });
    }

    private string Text(string key, string fallback, params object?[] arguments) =>
        _text.GetText(key, fallback, arguments);
}
