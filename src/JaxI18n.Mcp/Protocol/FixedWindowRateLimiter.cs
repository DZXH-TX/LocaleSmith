namespace JaxI18n.Mcp.Protocol;

internal sealed class FixedWindowRateLimiter
{
    private readonly object _sync = new();
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _windowStarted;
    private int _count;

    public FixedWindowRateLimiter(int limit, TimeSpan window, TimeProvider timeProvider)
    {
        _limit = limit;
        _window = window;
        _timeProvider = timeProvider;
        _windowStarted = timeProvider.GetUtcNow();
    }

    public bool TryAcquire(out TimeSpan retryAfter)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            var elapsed = now - _windowStarted;
            if (elapsed >= _window || elapsed < TimeSpan.Zero)
            {
                _windowStarted = now;
                _count = 0;
                elapsed = TimeSpan.Zero;
            }

            if (_count >= _limit)
            {
                retryAfter = _window - elapsed;
                return false;
            }

            _count++;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}
