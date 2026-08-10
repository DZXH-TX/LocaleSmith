using JaxI18n.Application.Models;
using JaxI18n.Application.Services;

namespace JaxI18n.Application.Abstractions;

public interface IPipelineJobScheduler : IAsyncDisposable
{
    event EventHandler<PipelineProgress>? ProgressChanged;

    ValueTask<PipelineJobHandle> EnqueueAsync(
        PipelineRequest request,
        CancellationToken cancellationToken = default);
}
