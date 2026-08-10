using JaxI18n.Presentation.Abstractions;
using Microsoft.UI.Dispatching;

namespace JaxI18n.App.Services;

public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _dispatcherQueue;

    public DispatcherQueueUiDispatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(() => action()))
        {
            throw new InvalidOperationException("The UI dispatcher is shutting down.");
        }
    }
}
