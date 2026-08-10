using System.Threading.Channels;
using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Models;

namespace LocaleSmith.Application.Services;

public sealed class PipelineJobScheduler : IPipelineJobScheduler
{
    private readonly TranslationPipeline _pipeline;
    private readonly Channel<QueueItem> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _disposeState;

    public PipelineJobScheduler(TranslationPipeline pipeline, int capacity = 128)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _pipeline = pipeline;
        _channel = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ProcessQueueAsync);
    }

    public event EventHandler<PipelineProgress>? ProgressChanged;

    public async ValueTask<PipelineJobHandle> EnqueueAsync(
        PipelineRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeState) != 0,
            this);

        var item = new QueueItem(Guid.NewGuid(), request, cancellationToken);
        try
        {
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            item.Dispose();
            throw;
        }

        return new PipelineJobHandle(
            item.JobId,
            item.Completion.Task,
            item.Cancel,
            () => item.LatestProgress);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        _shutdown.Token,
                        item.Cancellation.Token);
                    var progress = new DirectProgress(value =>
                    {
                        item.RecordProgress(value);
                        RaiseProgress(value);
                    });
                    var result = await _pipeline
                        .ExecuteAsync(item.Request, item.JobId, progress, linkedCancellation.Token)
                        .ConfigureAwait(false);
                    item.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException exception)
                {
                    item.Completion.TrySetCanceled(exception.CancellationToken);
                }
                catch (Exception exception)
                {
                    item.Completion.TrySetException(exception);
                }
                finally
                {
                    item.Dispose();
                }
            }
        }
        finally
        {
            while (_channel.Reader.TryRead(out var item))
            {
                item.Completion.TrySetCanceled(_shutdown.Token);
                item.Dispose();
            }
        }
    }

    private void RaiseProgress(PipelineProgress progress)
    {
        var handlers = ProgressChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<PipelineProgress> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, progress);
            }
            catch
            {
                // Subscriber failures must not terminate the processing worker.
            }
        }
    }

    private sealed class QueueItem : IDisposable
    {
        private int _disposeState;

        public QueueItem(Guid jobId, PipelineRequest request, CancellationToken cancellationToken)
        {
            JobId = jobId;
            Request = request;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Completion = new TaskCompletionSource<PipelineResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Guid JobId { get; }

        public PipelineRequest Request { get; }

        public CancellationTokenSource Cancellation { get; }

        public TaskCompletionSource<PipelineResult> Completion { get; }

        private PipelineProgress? _latestProgress;

        public PipelineProgress? LatestProgress => Volatile.Read(ref _latestProgress);

        public void RecordProgress(PipelineProgress progress) =>
            Volatile.Write(ref _latestProgress, progress);

        public void Cancel()
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion may dispose the linked source between the state check and Cancel().
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }
    }

    private sealed class DirectProgress(Action<PipelineProgress> report) : IProgress<PipelineProgress>
    {
        public void Report(PipelineProgress value) => report(value);
    }
}
