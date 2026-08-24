using LocaleSmith.Core.Models;

namespace LocaleSmith.Application.Models;

/// <summary>
/// A public, content-free lifecycle event for a model run. These events intentionally exclude
/// model reasoning, message content, tool arguments/results, paths, commands, context, and exception text.
/// </summary>
public enum ModelRunEventKind
{
    ModelRoundStarted,
    ModelRoundCompleted,
    ToolStarted,
    ToolCompleted,
    ToolFailed,
    RunCompleted,
    RunFailed,
    RunCancelled
}

public sealed record ModelRunEvent(
    int Sequence,
    ModelRunEventKind Kind,
    int Round,
    string? ToolName = null,
    ModelTokenUsage? Usage = null);
