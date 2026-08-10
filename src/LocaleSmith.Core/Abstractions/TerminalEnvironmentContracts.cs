using LocaleSmith.Core.Models;

namespace LocaleSmith.Core.Abstractions;

public interface ITerminalEnvironmentDetector
{
    ValueTask<TerminalEnvironmentContext> DetectAsync(CancellationToken cancellationToken = default);
}

public interface ISystemPromptContextProvider
{
    ValueTask<string> BuildAsync(CancellationToken cancellationToken = default);
}
