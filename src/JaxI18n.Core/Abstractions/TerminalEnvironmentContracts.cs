using JaxI18n.Core.Models;

namespace JaxI18n.Core.Abstractions;

public interface ITerminalEnvironmentDetector
{
    ValueTask<TerminalEnvironmentContext> DetectAsync(CancellationToken cancellationToken = default);
}

public interface ISystemPromptContextProvider
{
    ValueTask<string> BuildAsync(CancellationToken cancellationToken = default);
}
