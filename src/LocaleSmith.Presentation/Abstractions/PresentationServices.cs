using LocaleSmith.Application.Models;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.Presentation.Abstractions;

public interface IAppConfigurationService
{
    Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(
        AppSettingsUpdate settings,
        CancellationToken cancellationToken = default);
}

public interface IOnboardingService
{
    Task CompleteAsync(OnboardingSubmission submission, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists the display language in both the encrypted configuration and the bootstrap preference
/// that must be available before WinUI loads application resources.
/// </summary>
public interface IAppDisplayLanguageService
{
    Task SaveDisplayLanguageAsync(string language, CancellationToken cancellationToken = default);
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
        string targetLanguage,
        CancellationToken cancellationToken = default);
}

public interface IUiTextProvider
{
    string GetText(string key, string fallback, params object?[] arguments);
}

public interface IModelAssistantService
{
    Task<ModelAssistantCompletion> CompleteAsync(
        string modelSourceId,
        IReadOnlyList<ModelMessage> conversation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a project-aware assistant turn and reports only public lifecycle events. Implementations
    /// must never place provider-private reasoning, raw tool arguments/results, credentials, or
    /// unsanitized machine context in <paramref name="progress"/>.
    /// </summary>
    Task<ModelAssistantCompletion> CompleteAsync(
        string modelSourceId,
        IReadOnlyList<ModelMessage> conversation,
        ModProjectSnapshot? project,
        IProgress<ModelRunEvent>? progress,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(modelSourceId, conversation, cancellationToken);

    /// <summary>
    /// Runs a project-aware turn with a user-controlled, one-turn authorization gate for project
    /// mutations. Read-only project tools remain available when a project is selected.
    /// </summary>
    Task<ModelAssistantCompletion> CompleteAsync(
        string modelSourceId,
        IReadOnlyList<ModelMessage> conversation,
        ModProjectSnapshot? project,
        IProgress<ModelRunEvent>? progress,
        bool allowProjectChanges,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(modelSourceId, conversation, project, progress, cancellationToken);
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
