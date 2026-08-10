using System.Text.Json.Serialization;

namespace LocaleSmith.NativeInterop;

public sealed record ArchiveScanManifest
{
    public required uint SchemaVersion { get; init; }

    public required string CoreVersion { get; init; }

    public required NativeSourceArchive Source { get; init; }

    public NativeScanLimits? AppliedLimits { get; init; }

    public required NativeArchiveInventory Archive { get; init; }

    public required NativeModMetadata ModMetadata { get; init; }

    public required IReadOnlyList<NativeResourceEntry> Resources { get; init; }

    public NativeClassStringScan? ClassStringScan { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record NativeSourceArchive
{
    public required string Path { get; init; }

    public required string FileName { get; init; }

    public required ulong SizeBytes { get; init; }
}

public sealed record NativeScanLimits
{
    public ulong MaxArchiveSizeBytes { get; init; }

    public ulong MaxEntries { get; init; }

    public ulong MaxEntryUncompressedBytes { get; init; }

    public ulong MaxTotalUncompressedBytes { get; init; }

    public ulong MaxLoaderMetadataBytes { get; init; }

    public ulong MaxClassFileBytes { get; init; }

    public ulong MaxTotalClassScanBytes { get; init; }

    public ulong MaxClassStringReferences { get; init; }

    public ulong MaxClassStringOutputBytes { get; init; }
}

public sealed record NativeArchiveInventory
{
    public required ulong EntryCount { get; init; }

    public required ulong TotalCompressedBytes { get; init; }

    public required ulong TotalUncompressedBytes { get; init; }

    public string? ArchiveComment { get; init; }

    public bool ArchiveCommentWasLossy { get; init; }

    public required IReadOnlyList<NativeZipEntry> Entries { get; init; }

    public required NativeSignatureEvidence Signatures { get; init; }
}

public sealed record NativeZipEntry
{
    public required ulong Index { get; init; }

    public required string Path { get; init; }

    public string? NonUtf8NameHex { get; init; }

    public required string EntryType { get; init; }

    public required string CompressionMethod { get; init; }

    public required bool Encrypted { get; init; }

    public required uint Crc32 { get; init; }

    public required ulong CompressedSizeBytes { get; init; }

    public required ulong UncompressedSizeBytes { get; init; }

    public string? LastModified { get; init; }

    public uint? UnixMode { get; init; }

    public string? Comment { get; init; }

    public ulong ExtraDataSizeBytes { get; init; }

    public ulong HeaderOffsetBytes { get; init; }

    public ulong DataOffsetBytes { get; init; }
}

public sealed record NativeSignatureEvidence
{
    public required string Status { get; init; }

    public required bool ManifestPresent { get; init; }

    public required IReadOnlyList<string> SignatureFiles { get; init; }

    public required IReadOnlyList<string> SignatureBlocks { get; init; }

    public required bool CryptographicallyVerified { get; init; }

    public required bool ModificationBlockedByDefault { get; init; }

    public required string RepackWarning { get; init; }
}

public sealed record NativeModMetadata
{
    public required IReadOnlyList<string> DetectionPrecedence { get; init; }

    public IReadOnlyList<NativeMetadataSource> Sources { get; init; } = [];

    public string? PrimaryLoader { get; init; }

    public required string PrimaryModId { get; init; }

    public required IReadOnlyList<string> ModIds { get; init; }

    public required bool UsedFilenameFallback { get; init; }

    public required string FilenameFallbackNamespace { get; init; }
}

public sealed record NativeMetadataSource
{
    public required string Path { get; init; }

    public required string Loader { get; init; }

    public required IReadOnlyList<string> ModIds { get; init; }

    public required IReadOnlyList<string> RejectedModIds { get; init; }

    public string? ParseError { get; init; }
}

public sealed record NativeResourceEntry
{
    public required ulong ArchiveIndex { get; init; }

    public required string Path { get; init; }

    public required string Kind { get; init; }

    public string? Namespace { get; init; }

    public string? Locale { get; init; }

    public required uint Crc32 { get; init; }

    public required ulong CompressedSizeBytes { get; init; }

    public required ulong UncompressedSizeBytes { get; init; }
}

public sealed record NativeClassStringScan
{
    public required ulong DiscoveredClassCount { get; init; }

    public required ulong SuccessfulClassCount { get; init; }

    public required ulong FailedClassCount { get; init; }

    public required ulong TotalClassBytes { get; init; }

    public required IReadOnlyList<NativeClassFileSummary> Classes { get; init; }

    public required IReadOnlyList<NativeClassStringReference> References { get; init; }

    public required IReadOnlyList<NativeClassScanError> Errors { get; init; }

    public required string MutationPolicy { get; init; }
}

public sealed record NativeClassFileSummary
{
    public required ulong ArchiveIndex { get; init; }

    public required string ArchivePath { get; init; }

    public required string Class { get; init; }

    public required ushort MinorVersion { get; init; }

    public required ushort MajorVersion { get; init; }

    public required ulong StringReferenceCount { get; init; }
}

public sealed record NativeClassStringReference
{
    public required ulong ArchiveIndex { get; init; }

    public required string ArchivePath { get; init; }

    public required string Class { get; init; }

    public required string Method { get; init; }

    public required string Descriptor { get; init; }

    public required ulong BytecodeOffset { get; init; }

    public required string Opcode { get; init; }

    public required string Value { get; init; }

    public required ushort ConstantPoolIndex { get; init; }

    public required bool Candidate { get; init; }

    public string? RejectedReason { get; init; }
}

public sealed record NativeClassScanError
{
    public required ulong ArchiveIndex { get; init; }

    public required string ArchivePath { get; init; }

    public required string Error { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ArchiveScanManifest))]
internal sealed partial class NativeManifestJsonContext : JsonSerializerContext
{
}
