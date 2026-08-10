use serde::Serialize;

/// Current JSON manifest schema version.
pub const MANIFEST_SCHEMA_VERSION: u32 = 1;

/// Conservative resource limits applied before selected entries are read.
#[derive(Debug, Clone, Copy, Serialize)]
pub struct ScanLimits {
    /// Maximum size of the source archive on disk.
    pub max_archive_size_bytes: u64,
    /// Maximum number of central-directory entries.
    pub max_entries: u64,
    /// Maximum declared uncompressed size of one entry.
    pub max_entry_uncompressed_bytes: u64,
    /// Maximum sum of all declared uncompressed entry sizes.
    pub max_total_uncompressed_bytes: u64,
    /// Maximum uncompressed size of a loader metadata file that is read.
    pub max_loader_metadata_bytes: u64,
    /// Maximum uncompressed size of one `.class` file inspected in memory.
    pub max_class_file_bytes: u64,
    /// Maximum sum of declared uncompressed `.class` sizes inspected.
    pub max_total_class_scan_bytes: u64,
    /// Maximum number of LDC string references emitted into the manifest.
    pub max_class_string_references: u64,
    /// Maximum sum of UTF-8 text bytes duplicated into emitted references.
    pub max_class_string_output_bytes: u64,
}

impl Default for ScanLimits {
    fn default() -> Self {
        Self {
            max_archive_size_bytes: 1_073_741_824, // 1 GiB
            max_entries: 50_000,
            max_entry_uncompressed_bytes: 536_870_912, // 512 MiB
            max_total_uncompressed_bytes: 4_294_967_296, // 4 GiB
            max_loader_metadata_bytes: 1_048_576,      // 1 MiB
            max_class_file_bytes: 16_777_216,          // 16 MiB
            max_total_class_scan_bytes: 268_435_456,   // 256 MiB
            max_class_string_references: 200_000,
            max_class_string_output_bytes: 67_108_864, // 64 MiB
        }
    }
}

/// Complete, versioned result of scanning one ZIP/JAR file.
#[derive(Debug, Serialize)]
pub struct ScanManifest {
    /// Schema version used by this JSON document.
    pub schema_version: u32,
    /// Native library package version.
    pub core_version: &'static str,
    /// Source file identity.
    pub source: SourceArchive,
    /// Limits used for this scan.
    pub applied_limits: ScanLimits,
    /// ZIP central-directory inventory and signature evidence.
    pub archive: ArchiveInventory,
    /// Loader metadata and resolved namespace.
    pub mod_metadata: ModMetadataSummary,
    /// Translation-relevant resource files.
    pub resources: Vec<ResourceEntry>,
    /// Optional read-only Java bytecode string-reference scan.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub class_string_scan: Option<ClassStringScan>,
    /// Non-fatal parse or compatibility diagnostics.
    pub warnings: Vec<String>,
}

/// Identity of the source archive.
#[derive(Debug, Serialize)]
pub struct SourceArchive {
    /// Absolute canonical path when the platform can resolve it.
    pub path: String,
    /// Final path component.
    pub file_name: String,
    /// Source file length.
    pub size_bytes: u64,
}

/// ZIP-level inventory.
#[derive(Debug, Serialize)]
pub struct ArchiveInventory {
    /// Central-directory entry count.
    pub entry_count: u64,
    /// Sum of declared compressed sizes.
    pub total_compressed_bytes: u64,
    /// Sum of declared uncompressed sizes.
    pub total_uncompressed_bytes: u64,
    /// Archive comment decoded lossily when it is not UTF-8.
    pub archive_comment: Option<String>,
    /// Whether lossy decoding was needed for the archive comment.
    pub archive_comment_was_lossy: bool,
    /// Metadata needed to audit or deliberately reproduce entry settings.
    pub entries: Vec<ZipEntryRecord>,
    /// JAR signature-file evidence and the required modification policy.
    pub signatures: SignatureEvidence,
}

/// Central-directory metadata for one ZIP entry.
#[derive(Debug, Serialize)]
pub struct ZipEntryRecord {
    /// Zero-based central-directory index.
    pub index: u64,
    /// Validated, `/`-separated relative path.
    pub path: String,
    /// Raw filename bytes when they are not UTF-8, encoded as lowercase hex.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub non_utf8_name_hex: Option<String>,
    /// File or directory.
    pub entry_type: ZipEntryType,
    /// Compression method reported by the ZIP central directory.
    pub compression_method: String,
    /// Whether the entry carries a ZIP encryption flag.
    pub encrypted: bool,
    /// Declared CRC-32.
    pub crc32: u32,
    /// Declared compressed byte length.
    pub compressed_size_bytes: u64,
    /// Declared uncompressed byte length.
    pub uncompressed_size_bytes: u64,
    /// MS-DOS timestamp rendered by the ZIP library, if valid.
    pub last_modified: Option<String>,
    /// Unix permission/mode bits when present.
    pub unix_mode: Option<u32>,
    /// Per-entry comment.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub comment: Option<String>,
    /// Number of central/local extra-field bytes exposed by the reader.
    pub extra_data_size_bytes: u64,
    /// Offset of the local file header.
    pub header_offset_bytes: u64,
    /// Offset of the entry payload.
    pub data_offset_bytes: u64,
}

/// ZIP entry type accepted by the scanner.
#[derive(Debug, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ZipEntryType {
    /// Regular file.
    File,
    /// Directory marker.
    Directory,
}

/// Evidence of JAR signing material. This is not cryptographic verification.
#[derive(Debug, Serialize)]
pub struct SignatureEvidence {
    /// Coarse evidence state.
    pub status: SignatureEvidenceStatus,
    /// Whether `META-INF/MANIFEST.MF` exists.
    pub manifest_present: bool,
    /// `META-INF/*.SF` paths.
    pub signature_files: Vec<String>,
    /// `META-INF/*.RSA`, `*.DSA`, or `*.EC` paths.
    pub signature_blocks: Vec<String>,
    /// Always false: this scanner does not validate certificate chains/digests.
    pub cryptographically_verified: bool,
    /// True whenever signature material exists, including incomplete material.
    pub modification_blocked_by_default: bool,
    /// Explicitly states the repack/signature boundary.
    pub repack_warning: &'static str,
}

/// Coarse signature evidence state.
#[derive(Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum SignatureEvidenceStatus {
    /// No JAR signature files were observed.
    None,
    /// Only one half of expected signature material was observed.
    IncompleteUnverified,
    /// Both a signature file and block exist, but were not verified.
    PresentUnverified,
}

/// Loader metadata summary and resolved mod namespace.
#[derive(Debug, Serialize)]
pub struct ModMetadataSummary {
    /// Deterministic metadata precedence used by this version.
    pub detection_precedence: Vec<&'static str>,
    /// Every metadata source observed in precedence order.
    pub sources: Vec<MetadataSource>,
    /// Loader of the first source that produced a usable ID.
    pub primary_loader: Option<ModLoader>,
    /// First usable metadata ID, or sanitized filename fallback.
    pub primary_mod_id: String,
    /// All unique usable IDs in precedence order.
    pub mod_ids: Vec<String>,
    /// Whether `primary_mod_id` came from the JAR filename.
    pub used_filename_fallback: bool,
    /// The deterministic sanitized fallback, even when it was not needed.
    pub filename_fallback_namespace: String,
}

/// Result of parsing one loader metadata file.
#[derive(Debug, Serialize)]
pub struct MetadataSource {
    /// Archive-relative metadata path.
    pub path: String,
    /// Loader associated with the path.
    pub loader: ModLoader,
    /// Safe IDs accepted from this source.
    pub mod_ids: Vec<String>,
    /// Non-empty IDs rejected because they cannot be a safe asset namespace.
    pub rejected_mod_ids: Vec<String>,
    /// Parse/read error retained as a non-fatal diagnostic.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub parse_error: Option<String>,
}

/// Supported mod loader families.
#[derive(Debug, Clone, Copy, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum ModLoader {
    /// Fabric Loader (`fabric.mod.json`).
    Fabric,
    /// Forge (`META-INF/mods.toml` or legacy `mcmod.info`).
    Forge,
    /// NeoForge (`META-INF/neoforge.mods.toml`).
    NeoForge,
    /// Quilt Loader (`quilt.mod.json`).
    Quilt,
}

/// One translation-relevant file found in the archive.
#[derive(Debug, Serialize)]
pub struct ResourceEntry {
    /// Central-directory index for traceable read/replace operations.
    pub archive_index: u64,
    /// Original archive-relative path.
    pub path: String,
    /// Resource category.
    pub kind: ResourceKind,
    /// Asset namespace for language files.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub namespace: Option<String>,
    /// Locale filename stem for language files.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub locale: Option<String>,
    /// Declared CRC-32.
    pub crc32: u32,
    /// Declared compressed size.
    pub compressed_size_bytes: u64,
    /// Declared uncompressed size.
    pub uncompressed_size_bytes: u64,
}

/// Translation-relevant resource category.
#[derive(Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum ResourceKind {
    /// Modern JSON Minecraft language file.
    LanguageJson,
    /// Legacy Forge/Minecraft `.lang` language file.
    LanguageLang,
    /// Legacy resource-pack description file.
    PackText,
    /// JSON metadata sidecar such as `pack.mcmeta`.
    Mcmeta,
}

/// Read-only Java classfile scan results.
#[derive(Debug, Serialize)]
pub struct ClassStringScan {
    /// Number of `.class` files discovered in the archive.
    pub discovered_class_count: u64,
    /// Number of classfiles parsed successfully.
    pub successful_class_count: u64,
    /// Number of malformed, unreadable, or unsupported classfiles.
    pub failed_class_count: u64,
    /// Sum of declared uncompressed classfile sizes selected for scanning.
    pub total_class_bytes: u64,
    /// Structural summary for successfully parsed classfiles.
    pub classes: Vec<ClassFileSummary>,
    /// Every string constant referenced by a decoded `ldc`/`ldc_w` instruction.
    pub references: Vec<ClassStringReference>,
    /// Per-class failures retained instead of panicking or rewriting bytes.
    pub errors: Vec<ClassScanError>,
    /// Explicit statement of the mutation boundary.
    pub mutation_policy: &'static str,
}

/// Structural summary of one successfully parsed classfile.
#[derive(Debug, Serialize)]
pub struct ClassFileSummary {
    /// Central-directory index.
    pub archive_index: u64,
    /// Archive-relative `.class` path.
    pub archive_path: String,
    /// Internal JVM class name, for example `com/example/Demo`.
    pub class: String,
    /// Classfile minor version.
    pub minor_version: u16,
    /// Classfile major version.
    pub major_version: u16,
    /// Number of decoded LDC string references in this class.
    pub string_reference_count: u64,
}

/// One string constant loaded by JVM bytecode.
#[derive(Debug, Serialize)]
pub struct ClassStringReference {
    /// Central-directory index of the owning classfile.
    pub archive_index: u64,
    /// Archive-relative path of the owning classfile.
    pub archive_path: String,
    /// Internal JVM class name.
    pub class: String,
    /// JVM method name (`<init>` and `<clinit>` are preserved).
    pub method: String,
    /// JVM method descriptor.
    pub descriptor: String,
    /// Byte offset of the `ldc`/`ldc_w` opcode within the Code array.
    pub bytecode_offset: u64,
    /// Decoded opcode mnemonic (`ldc` or `ldc_w`).
    pub opcode: &'static str,
    /// Referenced `CONSTANT_String` value decoded from modified UTF-8.
    pub value: String,
    /// One-based JVM constant-pool index used by the instruction.
    pub constant_pool_index: u16,
    /// Conservative indication that the value may be user-facing text.
    pub candidate: bool,
    /// Machine-readable reason for rejection when `candidate` is false.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rejected_reason: Option<&'static str>,
}

/// Non-fatal classfile scan error.
#[derive(Debug, Serialize)]
pub struct ClassScanError {
    /// Central-directory index.
    pub archive_index: u64,
    /// Archive-relative `.class` path.
    pub archive_path: String,
    /// Bounded parser/decompression diagnostic.
    pub error: String,
}
