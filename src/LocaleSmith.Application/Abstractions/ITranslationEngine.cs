using LocaleSmith.Core.Models;

namespace LocaleSmith.Application.Abstractions;

public interface ITranslationEngine
{
    string TranslationContractVersion { get; }

    Task<TranslationBatchResult> TranslateAsync(
        TranslationBatchRequest request,
        CancellationToken cancellationToken);

    Task<TranslationBatchResult> TranslateAsync(
        TranslationBatchRequest request,
        IProgress<ModelTokenUsage>? usageProgress,
        CancellationToken cancellationToken) =>
        TranslateAsync(request, cancellationToken);
}
