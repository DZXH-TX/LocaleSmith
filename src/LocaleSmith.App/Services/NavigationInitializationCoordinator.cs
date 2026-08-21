namespace LocaleSmith.App.Services;

/// <summary>
/// Serializes page initialization across navigation cycles. When a page is re-entered while
/// the previous cycle is still cancelling, the new cycle waits for that run and starts one
/// replacement only when the stale run did not complete successfully.
/// </summary>
internal sealed class NavigationInitializationCoordinator
{
    private readonly object _gate = new();
    private InitializationRun? _activeRun;
    private int _navigationGeneration;
    private bool _isNavigated;
    private bool _isInitialized;

    public bool IsInitialized
    {
        get
        {
            lock (_gate)
            {
                return _isInitialized;
            }
        }
    }

    public async Task ActivateAsync(Func<Task<bool>> initializeAsync)
    {
        ArgumentNullException.ThrowIfNull(initializeAsync);

        int navigationGeneration;
        InitializationRun run;
        bool joinedPreviousNavigation;
        lock (_gate)
        {
            if (!_isNavigated)
            {
                _isNavigated = true;
                _navigationGeneration++;
            }

            navigationGeneration = _navigationGeneration;
            if (_isInitialized)
            {
                return;
            }

            run = _activeRun ?? StartRun(navigationGeneration, initializeAsync);
            joinedPreviousNavigation = run.NavigationGeneration != navigationGeneration;
        }

        var initialized = await run.Completion.ConfigureAwait(true);
        InitializationRun replacementRun;
        lock (_gate)
        {
            if (!IsCurrentNavigation(navigationGeneration))
            {
                return;
            }

            if (initialized)
            {
                _isInitialized = true;
                return;
            }

            if (!joinedPreviousNavigation)
            {
                return;
            }

            replacementRun = _activeRun ?? StartRun(navigationGeneration, initializeAsync);
        }

        var replacementInitialized = await replacementRun.Completion.ConfigureAwait(true);
        lock (_gate)
        {
            if (replacementInitialized && IsCurrentNavigation(navigationGeneration))
            {
                _isInitialized = true;
            }
        }
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            _isNavigated = false;
        }
    }

    private InitializationRun StartRun(
        int navigationGeneration,
        Func<Task<bool>> initializeAsync)
    {
        var run = new InitializationRun(navigationGeneration);
        _activeRun = run;
        run.Completion = ExecuteRunAsync(run, initializeAsync);
        return run;
    }

    private async Task<bool> ExecuteRunAsync(
        InitializationRun run,
        Func<Task<bool>> initializeAsync)
    {
        try
        {
            return await initializeAsync().ConfigureAwait(true);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeRun, run))
                {
                    _activeRun = null;
                }
            }
        }
    }

    private bool IsCurrentNavigation(int navigationGeneration) =>
        _isNavigated && _navigationGeneration == navigationGeneration;

    private sealed class InitializationRun(int navigationGeneration)
    {
        public int NavigationGeneration { get; } = navigationGeneration;

        public Task<bool> Completion { get; set; } = null!;
    }
}
