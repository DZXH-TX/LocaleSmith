using JaxI18n.Application.Models;

namespace JaxI18n.Application.Abstractions;

public interface ITranslationMemoryStore
{
    Task<TranslationMemorySnapshot> LoadAsync(
        TranslationMemoryKey key,
        CancellationToken cancellationToken);

    Task SaveAsync(
        TranslationMemorySnapshot snapshot,
        CancellationToken cancellationToken);
}
