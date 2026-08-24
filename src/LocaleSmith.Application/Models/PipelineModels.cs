using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;

namespace LocaleSmith.Application.Models;

public enum ArchiveSignatureState
{
    None,
    PresentUnverified,
    Valid,
    Invalid
}

public enum SignedArchiveHandling
{
    Block,
    CreateUnsignedCopy,
    Resign
}

public enum HardcodedStringMode
{
    ScanOnly,
    ExternalizeRecognizedSafePatterns
}

public enum PipelineStage
{
    Queued,
    Inspecting,
    Extracting,
    Analyzing,
    Translating,
    Writing,
    Repacking,
    Verifying,
    Committing,
    RollingBack,
    Completed,
    Failed,
    Cancelled
}

public enum PipelineStageStatus
{
    Pending,
    Current,
    Completed,
    Failed,
    Cancelled,
    Skipped
}

public sealed record PipelineRequest
{
    public PipelineRequest(
        string sourcePath,
        string outputPath,
        string targetLanguage = TranslationLanguageCatalog.DefaultLocale,
        IReadOnlySet<TranslationStyle>? styles = null,
        SignedArchiveHandling signedArchiveHandling = SignedArchiveHandling.Block,
        HardcodedStringMode hardcodedStringMode = HardcodedStringMode.ScanOnly,
        string? modelSourceId = null,
        Guid? requestedJobId = null,
        int maxOutputTokens = ModelSource.DefaultMaxOutputTokens,
        int maxSourceCharactersPerRequest = ModelSource.DefaultMaxSourceCharactersPerRequest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        SourcePath = Path.GetFullPath(sourcePath);
        OutputPath = Path.GetFullPath(outputPath);
        if (string.Equals(SourcePath, OutputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The source and output paths must be different.", nameof(outputPath));
        }

        TargetLanguage = TranslationLanguageCatalog.NormalizeLocale(targetLanguage);
        Styles = styles is null
            ? new HashSet<TranslationStyle> { TranslationStyle.Formal }
            : new HashSet<TranslationStyle>(styles);
        if (Styles.Count != 1 || !Enum.IsDefined(Styles.Single()))
        {
            throw new ArgumentException("Exactly one valid translation style is required per pipeline job.", nameof(styles));
        }

        SignedArchiveHandling = signedArchiveHandling;
        HardcodedStringMode = hardcodedStringMode;
        ModelSourceId = string.IsNullOrWhiteSpace(modelSourceId) ? null : modelSourceId.Trim();
        if (maxOutputTokens is < ModelSource.MinimumMaxOutputTokens or > ModelSource.MaximumMaxOutputTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        }

        if (maxSourceCharactersPerRequest is
            < ModelSource.MinimumMaxSourceCharactersPerRequest or
            > ModelSource.MaximumMaxSourceCharactersPerRequest)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSourceCharactersPerRequest));
        }

        MaxOutputTokens = maxOutputTokens;
        MaxSourceCharactersPerRequest = maxSourceCharactersPerRequest;
        if (requestedJobId == Guid.Empty)
        {
            throw new ArgumentException("A requested pipeline job identifier cannot be empty.", nameof(requestedJobId));
        }

        RequestedJobId = requestedJobId;
    }

    public string SourcePath { get; }

    public string OutputPath { get; }

    public string TargetLanguage { get; }

    public IReadOnlySet<TranslationStyle> Styles { get; }

    public SignedArchiveHandling SignedArchiveHandling { get; }

    public HardcodedStringMode HardcodedStringMode { get; }

    public string? ModelSourceId { get; }

    public int MaxOutputTokens { get; }

    public int MaxSourceCharactersPerRequest { get; }

    /// <summary>
    /// Optional caller-owned identity used when diagnostics must be established before a job is
    /// accepted by the scheduler. Direct pipeline callers can leave this unset.
    /// </summary>
    public Guid? RequestedJobId { get; }
}

public sealed record ArchiveInspection(
    string PackageIdentity,
    string ModId,
    string Loader,
    bool UsedFileNameFallback,
    ArchiveSignatureState SignatureState,
    bool CanResign,
    IReadOnlyList<string> Warnings,
    MinecraftContentKind ContentKind = MinecraftContentKind.Unknown,
    ulong EntryCount = 0,
    int ResourceCount = 0,
    string SignatureStatus = "none")
{
    public bool IsSigned => SignatureState != ArchiveSignatureState.None;
}

public sealed record HardcodedStringCandidate(
    ulong ArchiveIndex,
    string ArchivePath,
    string ClassName,
    string MethodName,
    string MethodDescriptor,
    int BytecodeOffset,
    string Opcode,
    ushort ConstantPoolIndex,
    string Value,
    string SuggestedKey,
    bool IsRecognizedSafePattern);

public sealed record ExternalizationReport(
    int CandidateCount,
    int ExternalizedCount,
    IReadOnlyList<string> Warnings);

public sealed record PackageArtifact(TranslationStyle Style, string Path);

/// <summary>
/// Identifies the strongest verification actually completed for an output artifact. Static
/// analysis of a precompiled JAR is intentionally distinct from compiling source code.
/// </summary>
public enum ArtifactValidationMode
{
    ArchiveStaticAnalysis,
    PrecompiledJarStaticAnalysis
}

public sealed record PackageVerification(
    bool IsValidArchive,
    bool MetadataPreserved,
    IReadOnlyList<string> Errors,
    IReadOnlyList<PackageArtifact>? Artifacts = null,
    ArtifactValidationMode ValidationMode = ArtifactValidationMode.ArchiveStaticAnalysis,
    bool SourceCompilationPerformed = false,
    IReadOnlyList<string>? CompletedChecks = null,
    IReadOnlyList<string>? Warnings = null);

public sealed record PipelineProgress(
    Guid JobId,
    PipelineStage Stage,
    double Fraction,
    string Message,
    PipelineStage? NextStage = null,
    IReadOnlyList<PipelineStageProgress>? Stages = null,
    PipelineStageStatus? RollbackStatus = null,
    ModelTokenUsage? ModelUsage = null);

public sealed record PipelineStageProgress(
    PipelineStage Stage,
    PipelineStageStatus Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc);

public sealed record PipelineResult(
    Guid JobId,
    string OutputPath,
    ArchiveInspection Inspection,
    int SourceEntryCount,
    int TranslatedEntryCount,
    int ReusedEntryCount,
    IReadOnlyList<HardcodedStringCandidate> HardcodedCandidates,
    ExternalizationReport Externalization,
    IReadOnlyList<PackageArtifact> Artifacts,
    PackageVerification Verification,
    IReadOnlyList<string> Warnings,
    ModelTokenUsage? ModelUsage = null);
