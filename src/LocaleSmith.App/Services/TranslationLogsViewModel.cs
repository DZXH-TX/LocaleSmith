using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.App.Services;

public sealed class TranslationLogsViewModel : ViewModelBase, IDisposable
{
    private readonly TranslationLogService _logService;
    private readonly IUiTextProvider _text;
    private readonly IUiDispatcher _dispatcher;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingJobIds = new();
    private TranslationLogSessionInfo? _selectedSession;
    private TranslationLogViewMode _selectedView = TranslationLogViewMode.Debug;
    private string _logText = string.Empty;
    private string _logDirectoryPath = string.Empty;
    private int _notificationQueued;
    private bool _suppressSelectionLoad;
    private long _selectionVersion;
    private bool _isActive;
    private int _selectionLoadCount;
    private bool _disposed;

    public TranslationLogsViewModel(
        TranslationLogService logService,
        IUiTextProvider text,
        IUiDispatcher dispatcher)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        RefreshCommand = new AsyncRelayCommand((Func<Task>)RefreshAsync, () => !IsBusy);
        _logService.LogsChanged += OnLogsChanged;
    }

    public ObservableCollection<TranslationLogSessionInfo> Sessions { get; } = [];

    public IReadOnlyList<TranslationLogViewMode> ViewOptions { get; } =
        Enum.GetValues<TranslationLogViewMode>();

    public IAsyncRelayCommand RefreshCommand { get; }

    public TranslationLogSessionInfo? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (!SetProperty(ref _selectedSession, value) || _suppressSelectionLoad)
            {
                if (_suppressSelectionLoad)
                {
                    Interlocked.Increment(ref _selectionVersion);
                }
                return;
            }

            Interlocked.Increment(ref _selectionVersion);
            LogText = string.Empty;
            _ = LoadSelectedAsync();
        }
    }

    public TranslationLogViewMode SelectedView
    {
        get => _selectedView;
        set
        {
            if (!SetProperty(ref _selectedView, value))
            {
                return;
            }

            Interlocked.Increment(ref _selectionVersion);
            LogText = string.Empty;
            _ = LoadSelectedAsync();
        }
    }

    public string LogText
    {
        get => _logText;
        private set
        {
            if (SetProperty(ref _logText, value))
            {
                OnPropertyChanged(nameof(HasLogText));
                NotifyVisualStateProperties();
            }
        }
    }

    public string LogDirectoryPath
    {
        get => _logDirectoryPath;
        private set => SetProperty(ref _logDirectoryPath, value);
    }

    public bool HasSessions => Sessions.Count > 0;

    public bool HasLogText => !string.IsNullOrWhiteSpace(LogText);

    public bool IsLoading => IsBusy || Volatile.Read(ref _selectionLoadCount) > 0;

    public bool ShowEmptyState => !IsLoading && !HasError && !HasLogText;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);

    public void SetActive(bool isActive)
    {
        if (_disposed)
        {
            return;
        }

        _isActive = isActive;
        if (!isActive)
        {
            _pendingJobIds.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logService.LogsChanged -= OnLogsChanged;
        _disposeCancellation.Cancel();
        // The dispatcher may already own a queued refresh callback. Keep these small synchronization
        // primitives alive until process teardown so a late callback can observe cancellation safely.
    }

    private Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            IsBusy = true;
            NotifyVisualStateProperties();
            RefreshCommand.NotifyCanExecuteChanged();
            try
            {
                var selectedJobId = SelectedSession?.JobId;
                LogDirectoryPath = await _logService
                    .GetConfiguredDirectoryAsync(cancellationToken)
                    .ConfigureAwait(true);
                var sessions = await _logService
                    .GetSessionsAsync(cancellationToken)
                    .ConfigureAwait(true);

                _suppressSelectionLoad = true;
                try
                {
                    Sessions.Clear();
                    foreach (var session in sessions)
                    {
                        Sessions.Add(session);
                    }

                    OnPropertyChanged(nameof(HasSessions));
                    SelectedSession = selectedJobId is { } jobId
                        ? Sessions.FirstOrDefault(session => session.JobId == jobId) ?? Sessions.FirstOrDefault()
                        : Sessions.FirstOrDefault();
                }
                finally
                {
                    _suppressSelectionLoad = false;
                }

                await ReadSelectedCoreAsync(cancellationToken).ConfigureAwait(true);
                ClearLoadError();
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                SetLoadError();
                LogText = string.Empty;
            }
            finally
            {
                IsBusy = false;
                RefreshCommand.NotifyCanExecuteChanged();
                NotifyVisualStateProperties();
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task LoadSelectedAsync()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _selectionLoadCount);
        NotifyVisualStateProperties();
        try
        {
            await _refreshGate.WaitAsync(_disposeCancellation.Token).ConfigureAwait(true);
            try
            {
                await ReadSelectedCoreAsync(_disposeCancellation.Token).ConfigureAwait(true);
                ClearLoadError();
            }
            finally
            {
                _refreshGate.Release();
            }
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetLoadError();
            LogText = string.Empty;
        }
        finally
        {
            Interlocked.Decrement(ref _selectionLoadCount);
            NotifyVisualStateProperties();
        }
    }

    private async Task ReadSelectedCoreAsync(CancellationToken cancellationToken)
    {
        var version = Volatile.Read(ref _selectionVersion);
        var selected = SelectedSession;
        var view = SelectedView;
        var content = selected is null
            ? string.Empty
            : await _logService.ReadAsync(selected, view, cancellationToken).ConfigureAwait(true);
        if (version == Volatile.Read(ref _selectionVersion) &&
            selected?.JobId == SelectedSession?.JobId &&
            view == SelectedView)
        {
            LogText = content;
        }
    }

    private void OnLogsChanged(object? sender, TranslationLogChangedEventArgs args)
    {
        if (_disposed || !_isActive)
        {
            return;
        }

        _pendingJobIds[args.JobId] = 0;
        ScheduleNotificationRefresh();
    }

    private void ScheduleNotificationRefresh()
    {
        if (_disposed || !_isActive || Interlocked.Exchange(ref _notificationQueued, 1) != 0)
        {
            return;
        }

        _ = QueueNotificationRefreshAsync();
    }

    private async Task QueueNotificationRefreshAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), _disposeCancellation.Token).ConfigureAwait(false);
            _dispatcher.Post(RefreshFromNotification);
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _notificationQueued, 0);
        }
        catch (Exception) when (_disposed)
        {
            Interlocked.Exchange(ref _notificationQueued, 0);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.Exchange(ref _notificationQueued, 0);
        }
    }

    private void RefreshFromNotification()
    {
        _ = RefreshFromNotificationAsync();
    }

    private async Task RefreshFromNotificationAsync()
    {
        var changedJobs = _pendingJobIds.Keys.ToArray();
        foreach (var jobId in changedJobs)
        {
            _pendingJobIds.TryRemove(jobId, out _);
        }

        Interlocked.Exchange(ref _notificationQueued, 0);
        try
        {
            if (_disposed || !_isActive)
            {
                return;
            }

            var knownJobs = Sessions.Select(static session => session.JobId).ToHashSet();
            if (changedJobs.Any(jobId => !knownJobs.Contains(jobId)))
            {
                await RefreshAsync(_disposeCancellation.Token).ConfigureAwait(true);
            }
            else if (SelectedSession is { } selected && changedJobs.Contains(selected.JobId))
            {
                await LoadSelectedAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!_disposed)
            {
                SetLoadError();
            }
        }
        finally
        {
            if (!_disposed && _isActive && !_pendingJobIds.IsEmpty)
            {
                ScheduleNotificationRefresh();
            }
        }
    }

    private void ClearLoadError()
    {
        ErrorMessage = null;
        NotifyVisualStateProperties();
    }

    private void SetLoadError()
    {
        ErrorMessage = _text.GetText(
            "LogsLoadFailed",
            "Translation logs could not be loaded. Check the configured directory and try again.");
        NotifyVisualStateProperties();
    }

    private void NotifyVisualStateProperties()
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowEmptyState));
    }
}
