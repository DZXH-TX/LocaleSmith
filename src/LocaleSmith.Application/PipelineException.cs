using LocaleSmith.Application.Models;

namespace LocaleSmith.Application;

public sealed class PipelineException : Exception
{
    public PipelineException(Guid jobId, PipelineStage failedStage, string message)
        : base(message)
    {
        JobId = jobId;
        FailedStage = failedStage;
    }

    public PipelineException(Guid jobId, PipelineStage failedStage, string message, Exception innerException)
        : base(message, innerException)
    {
        JobId = jobId;
        FailedStage = failedStage;
    }

    public Guid JobId { get; }

    public PipelineStage FailedStage { get; }
}
