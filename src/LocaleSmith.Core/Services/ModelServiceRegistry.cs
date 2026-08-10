using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Core.Services;

public sealed class ModelServiceRegistry : IModelServiceRegistry, IDisposable
{
    private readonly Dictionary<string, IModelService> _services = new(StringComparer.Ordinal);
    private readonly ReaderWriterLockSlim _lock = new();
    private string? _selectedId;
    private bool _disposed;

    public event EventHandler<ModelSelectionChangedEventArgs>? SelectionChanged;

    public IReadOnlyList<ModelSource> Sources
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                return _services.Values
                    .Select(static service => service.Source)
                    .OrderBy(static source => source.DisplayName, StringComparer.CurrentCulture)
                    .ThenBy(static source => source.Id, StringComparer.Ordinal)
                    .ToArray();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public ModelSource? SelectedSource
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                return _selectedId is not null && _services.TryGetValue(_selectedId, out var service)
                    ? service.Source
                    : null;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public void AddOrUpdate(IModelService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        ModelSource? previous = null;
        ModelSource? current = null;
        var selectionChanged = false;

        _lock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            _services.TryGetValue(service.Source.Id, out var existing);
            _services[service.Source.Id] = service;
            if (_selectedId is null)
            {
                _selectedId = service.Source.Id;
                current = service.Source;
                selectionChanged = true;
            }
            else if (string.Equals(_selectedId, service.Source.Id, StringComparison.Ordinal) && existing is not null)
            {
                previous = existing.Source;
                current = service.Source;
                selectionChanged = true;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        if (selectionChanged)
        {
            SelectionChanged?.Invoke(this, new ModelSelectionChangedEventArgs(previous, current));
        }
    }

    public bool Remove(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ModelSource? previous = null;
        ModelSource? current = null;
        var selectionChanged = false;
        bool removed;

        _lock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (!_services.Remove(sourceId, out var removedService))
            {
                return false;
            }

            removed = true;
            if (string.Equals(_selectedId, sourceId, StringComparison.Ordinal))
            {
                previous = removedService.Source;
                var replacement = _services.Values
                    .OrderBy(static service => service.Source.DisplayName, StringComparer.CurrentCulture)
                    .ThenBy(static service => service.Source.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                _selectedId = replacement?.Source.Id;
                current = replacement?.Source;
                selectionChanged = true;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        if (selectionChanged)
        {
            SelectionChanged?.Invoke(this, new ModelSelectionChangedEventArgs(previous, current));
        }

        return removed;
    }

    public bool SelectSource(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ModelSource? previous;
        ModelSource current;

        _lock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (!_services.TryGetValue(sourceId, out var next))
            {
                return false;
            }

            if (string.Equals(_selectedId, sourceId, StringComparison.Ordinal))
            {
                return true;
            }

            previous = _selectedId is not null && _services.TryGetValue(_selectedId, out var old)
                ? old.Source
                : null;
            _selectedId = sourceId;
            current = next.Source;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        SelectionChanged?.Invoke(this, new ModelSelectionChangedEventArgs(previous, current));
        return true;
    }

    public bool TryGet(string sourceId, out IModelService? service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            return _services.TryGetValue(sourceId, out service);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IModelService GetSelected()
    {
        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            if (_selectedId is null || !_services.TryGetValue(_selectedId, out var service))
            {
                throw new InvalidOperationException("No model source is currently selected.");
            }

            return service;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lock.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
