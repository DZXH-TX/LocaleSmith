using LocaleSmith.Application.Models;

namespace LocaleSmith.Application.Services;

public sealed class PipelineJobHandle
{
    private readonly Action _cancel;
    private readonly Func<PipelineProgress?> _getLatestProgress;

    internal PipelineJobHandle(
        Guid jobId,
        Task<PipelineResult> completion,
        Action cancel,
        Func<PipelineProgress?>? getLatestProgress = null)
    {
        JobId = jobId;
        Completion = completion;
        _cancel = cancel;
        _getLatestProgress = getLatestProgress ?? (() => null);
    }

    public Guid JobId { get; }

    public Task<PipelineResult> Completion { get; }

    /// <summary>
    /// Returns the most recently published immutable progress snapshot. This also covers progress that was
    /// published before a presentation subscriber could register the returned job identifier.
    /// </summary>
    public PipelineProgress? LatestProgress => _getLatestProgress();

    public void Cancel() => _cancel();
}
