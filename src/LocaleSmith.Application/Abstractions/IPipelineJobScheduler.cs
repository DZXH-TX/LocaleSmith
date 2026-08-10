using LocaleSmith.Application.Models;
using LocaleSmith.Application.Services;

namespace LocaleSmith.Application.Abstractions;

public interface IPipelineJobScheduler : IAsyncDisposable
{
    event EventHandler<PipelineProgress>? ProgressChanged;

    ValueTask<PipelineJobHandle> EnqueueAsync(
        PipelineRequest request,
        CancellationToken cancellationToken = default);
}
