using JaxI18n.Core.Models;

namespace JaxI18n.Application.Abstractions;

public interface ITranslationEngine
{
    string TranslationContractVersion { get; }

    Task<TranslationBatchResult> TranslateAsync(
        TranslationBatchRequest request,
        CancellationToken cancellationToken);
}
