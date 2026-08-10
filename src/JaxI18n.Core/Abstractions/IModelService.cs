using JaxI18n.Core.Models;

namespace JaxI18n.Core.Abstractions;

public interface IModelService
{
    ModelSource Source { get; }

    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

public interface IModelCatalogService
{
    Task<IReadOnlyList<AvailableModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default);
}

public interface IModelToolExecutor
{
    IReadOnlyList<ModelToolDefinition> Tools { get; }

    Task<ModelToolResult> ExecuteAsync(
        ModelToolCall toolCall,
        CancellationToken cancellationToken = default);
}
