using JaxI18n.Application.Models;
using JaxI18n.Core.Models;

namespace JaxI18n.Application.Abstractions;

public interface IArchiveWorkspaceBackend
{
    Task<IArchiveWorkspace> BeginAsync(
        Guid jobId,
        PipelineRequest request,
        CancellationToken cancellationToken);
}

public interface IArchiveWorkspace : IAsyncDisposable
{
    Task<ArchiveInspection> InspectAsync(CancellationToken cancellationToken);

    Task ExtractAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TranslationEntry>> ReadTranslatableEntriesAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<HardcodedStringCandidate>> ScanHardcodedStringsAsync(
        CancellationToken cancellationToken);

    Task<ExternalizationReport> ExternalizeAsync(
        IReadOnlyList<HardcodedStringCandidate> candidates,
        CancellationToken cancellationToken);

    Task ApplyTranslationsAsync(
        TranslationBatchResult translations,
        CancellationToken cancellationToken);

    Task<PackageVerification> StagePackageAsync(
        string outputPath,
        CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
