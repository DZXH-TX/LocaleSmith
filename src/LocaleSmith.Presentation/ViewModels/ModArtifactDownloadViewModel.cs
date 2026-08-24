using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Abstractions;

namespace LocaleSmith.Presentation.ViewModels;

public sealed class ModArtifactDownloadViewModel : ViewModelBase, IDisposable
{
    private readonly IModPlatformArtifactDownloadCoordinator _coordinator;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUiTextProvider _text;
    private CancellationTokenSource? _operation;
    private ModPlatformVersion? _artifact;
    private bool _isAccelerationAvailable;
    private bool _parallelRangeEnabled;
    private double _progress;
    private bool _disposed;

    public ModArtifactDownloadViewModel(
        IModPlatformArtifactDownloadCoordinator coordinator,
        IUiDispatcher dispatcher,
        IUiTextProvider? text = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _text = text ?? FallbackUiTextProvider.Instance;
    }

    public ModPlatformVersion? Artifact
    {
        get => _artifact;
        private set
        {
            if (SetProperty(ref _artifact, value))
            {
                OnPropertyChanged(nameof(HasArtifact));
                OnPropertyChanged(nameof(Filename));
                OnPropertyChanged(nameof(CanStartDownload));
            }
        }
    }

    public bool HasArtifact => Artifact is not null;

    public string? Filename => Artifact?.Filename;

    public bool CanStartDownload => HasArtifact && !IsBusy;

    public bool IsAccelerationAvailable
    {
        get => _isAccelerationAvailable;
        private set => SetProperty(ref _isAccelerationAvailable, value);
    }

    public bool ParallelRangeEnabled
    {
        get => _parallelRangeEnabled;
        private set => SetProperty(ref _parallelRangeEnabled, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public async Task SelectArtifactAsync(
        ModPlatformVersion? artifact,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelActiveOperation();
        Artifact = artifact;
        IsAccelerationAvailable = false;
        ParallelRangeEnabled = false;
        Progress = 0;
        ErrorMessage = null;
        StatusMessage = null;
        if (artifact is null)
        {
            return;
        }

        try
        {
            var availability = await _coordinator
                .GetAccelerationAvailabilityAsync(artifact, cancellationToken)
                .ConfigureAwait(true);
            IsAccelerationAvailable = availability.IsAvailable;
            ParallelRangeEnabled = availability.ParallelRangeEnabled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Availability is optional; the same-origin default source remains usable.
            IsAccelerationAvailable = false;
            ParallelRangeEnabled = false;
        }
    }

    public async Task DownloadAsync(
        string destinationPath,
        ModPlatformDownloadRoute route,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (Artifact is not { } artifact || IsBusy)
        {
            return;
        }

        if (route == ModPlatformDownloadRoute.DomesticAcceleration && !IsAccelerationAvailable)
        {
            route = ModPlatformDownloadRoute.Default;
        }

        _operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsBusy = true;
        OnPropertyChanged(nameof(CanStartDownload));
        ErrorMessage = null;
        StatusMessage = null;
        Progress = 0;
        try
        {
            var progress = new DispatcherProgress(_dispatcher, update =>
            {
                Progress = update.TotalBytes == 0
                    ? 0
                    : Math.Clamp((double)update.BytesReceived / update.TotalBytes, 0, 1);
            });
            var result = await _coordinator.DownloadAsync(
                artifact,
                destinationPath,
                route,
                progress,
                _operation.Token).ConfigureAwait(true);
            Progress = 1;
            StatusMessage = result.FellBack
                ? _text.GetText(
                    "CommunityDownloadFallbackStatus",
                    "The acceleration source was unavailable. The file was downloaded safely from the default source.")
                : _text.GetText(
                    "CommunityDownloadCompletedStatus",
                    "Download completed and SHA-256 verification passed.");
        }
        catch (OperationCanceledException) when (_operation.IsCancellationRequested)
        {
            StatusMessage = _text.GetText(
                "CommunityDownloadCancelledStatus",
                "Download cancelled. A credential-free partial file may be retained for resume.");
        }
        catch (Exception)
        {
            ErrorMessage = _text.GetText(
                "CommunityDownloadFailedError",
                "The download could not be completed or verified. No partial artifact was published.");
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            IsBusy = false;
            OnPropertyChanged(nameof(CanStartDownload));
        }
    }

    public void CancelActiveOperation() => _operation?.Cancel();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CancelActiveOperation();
        _operation?.Dispose();
        _operation = null;
        _disposed = true;
    }

    private sealed class DispatcherProgress(
        IUiDispatcher dispatcher,
        Action<ModPlatformDownloadProgress> update) : IProgress<ModPlatformDownloadProgress>
    {
        public void Report(ModPlatformDownloadProgress value) =>
            dispatcher.Post(() => update(value));
    }
}
