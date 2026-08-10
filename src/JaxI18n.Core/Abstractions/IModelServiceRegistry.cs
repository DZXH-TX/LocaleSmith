using JaxI18n.Core.Models;

namespace JaxI18n.Core.Abstractions;

public interface IModelServiceRegistry
{
    event EventHandler<ModelSelectionChangedEventArgs>? SelectionChanged;

    IReadOnlyList<ModelSource> Sources { get; }

    ModelSource? SelectedSource { get; }

    void AddOrUpdate(IModelService service);

    bool Remove(string sourceId);

    bool SelectSource(string sourceId);

    bool TryGet(string sourceId, out IModelService? service);

    IModelService GetSelected();
}

public sealed class ModelSelectionChangedEventArgs(ModelSource? previous, ModelSource? current) : EventArgs
{
    public ModelSource? Previous { get; } = previous;

    public ModelSource? Current { get; } = current;
}
