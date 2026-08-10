using LocaleSmith.Application.Models;

namespace LocaleSmith.Application.Abstractions;

public interface ITranslationMemoryStore
{
    Task<TranslationMemorySnapshot> LoadAsync(
        TranslationMemoryKey key,
        CancellationToken cancellationToken);

    Task SaveAsync(
        TranslationMemorySnapshot snapshot,
        CancellationToken cancellationToken);
}
