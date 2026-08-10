using JaxI18n.Core.Models;
using JaxI18n.Presentation.Models;

namespace JaxI18n.Presentation.Abstractions;

public interface IAppConfigurationService
{
    Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken = default);
}

public interface IOnboardingService
{
    Task CompleteAsync(OnboardingSubmission submission, CancellationToken cancellationToken = default);
}

public interface IModelSourceCatalog
{
    Task<IReadOnlyList<ModelSourceProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ModelSourceProfile> SaveAsync(
        ModelSourceDraft source,
        ReadOnlyMemory<char> apiKey,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string sourceId, CancellationToken cancellationToken = default);

    Task<ModelConnectionResult> TestConnectionAsync(
        ModelSourceDraft source,
        ReadOnlyMemory<char> apiKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AvailableModelInfo>> ListAvailableModelsAsync(
        ModelSourceDraft source,
        CancellationToken cancellationToken = default);
}

public interface IModelSelectionService
{
    IReadOnlyList<ModelSource> Sources { get; }

    ModelSource? SelectedSource { get; }

    Task<bool> SelectSourceAsync(string sourceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional change feed for model-selection services whose source catalog can be loaded or edited at runtime.
/// Consumers must still read the current <see cref="IModelSelectionService"/> snapshot when they subscribe.
/// </summary>
public interface IModelSelectionStateNotifier
{
    event EventHandler<ModelSelectionStateChangedEventArgs>? StateChanged;
}

public sealed class ModelSelectionStateChangedEventArgs(
    IReadOnlyList<ModelSource> sources,
    ModelSource? selectedSource) : EventArgs
{
    public IReadOnlyList<ModelSource> Sources { get; } =
        sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));

    public ModelSource? SelectedSource { get; } = selectedSource;
}

public interface ITranslationQueueService
{
    event EventHandler<TranslationQueueProgress>? ProgressChanged;

    ValueTask<TranslationQueueHandle> EnqueueAsync(
        TranslationQueueRequest request,
        CancellationToken cancellationToken = default);
}

public interface IUiDispatcher
{
    void Post(Action action);
}

public interface IOutputPathStrategy
{
    Task<string> CreateOutputPathAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}

public interface IUiTextProvider
{
    string GetText(string key, string fallback, params object?[] arguments);
}

public interface ICliDiagnosticRequestFactory
{
    Task<JaxI18n.Presentation.ViewModels.CliConfirmationViewModel> CreateAsync(
        string sandboxPath,
        CancellationToken cancellationToken = default);
}

public interface IModelAssistantService
{
    Task<ModelAssistantCompletion> CompleteAsync(
        string modelSourceId,
        IReadOnlyList<ModelMessage> conversation,
        CancellationToken cancellationToken = default);
}

public sealed class FallbackUiTextProvider : IUiTextProvider
{
    public static FallbackUiTextProvider Instance { get; } = new();

    private FallbackUiTextProvider()
    {
    }

    public string GetText(string key, string fallback, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(fallback);
        return arguments.Length == 0
            ? fallback
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, fallback, arguments);
    }
}
