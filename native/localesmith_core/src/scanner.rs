use std::{
    collections::{BTreeSet, HashMap, HashSet},
    fs::{self, File},
    io::{BufReader, Read, Seek, SeekFrom},
    path::Path,
};

use zip::ZipArchive;

use crate::{
    classfile::parse_classfile,
    error::CoreError,
    metadata::{DETECTION_PRECEDENCE, ParsedMetadata, parse_metadata, summarize_metadata},
    model::{
        ArchiveInventory, ClassScanError, ClassStringScan, MANIFEST_SCHEMA_VERSION, ResourceEntry,
        ResourceKind, ScanLimits, ScanManifest, SignatureEvidence, SignatureEvidenceStatus,
        SourceArchive, ZipEntryRecord, ZipEntryType,
    },
    path_safety::validate_archive_path,
};

const REPACK_WARNING: &str = "Signature material is recorded but not cryptographically verified. Any content-changing JAR repack invalidates an existing signature; modification is blocked by default and requires an explicit re-signing workflow.";
const CLASS_MUTATION_POLICY: &str =
    "read_only_candidates_only; bytecode is never rewritten by this scanner";

#[derive(Debug)]
struct ClassArchiveEntry {
    index: usize,
    path: String,
    declared_size: u64,
}

/// Scan a ZIP/JAR using conservative default limits.
pub fn scan_archive(path: impl AsRef<Path>) -> Result<ScanManifest, CoreError> {
    scan_archive_with_limits(path, ScanLimits::default())
}

/// Scan a ZIP/JAR using caller-supplied limits.
///
/// This function never extracts entries. It validates every central-directory
/// path as though it were going to be extracted onto Windows, inventories ZIP
/// metadata, reads only small loader metadata files, and returns a manifest.
pub fn scan_archive_with_limits(
    path: impl AsRef<Path>,
    limits: ScanLimits,
) -> Result<ScanManifest, CoreError> {
    let path = path.as_ref();
    validate_limits(limits)?;

    let source_metadata = fs::metadata(path).map_err(|source| CoreError::Io {
        path: path.to_path_buf(),
        source,
    })?;
    if !source_metadata.is_file() {
        return Err(CoreError::InvalidArgument(format!(
            "archive path is not a regular file: {}",
            path.display()
        )));
    }
    enforce_limit(
        "archive_size_bytes",
        limits.max_archive_size_bytes,
        source_metadata.len(),
    )?;

    let mut file = File::open(path).map_err(|source| CoreError::Io {
        path: path.to_path_buf(),
        source,
    })?;
    preflight_zip_entry_count(&mut file, path, limits.max_entries)?;
    file.seek(SeekFrom::Start(0))
        .map_err(|source| CoreError::Io {
            path: path.to_path_buf(),
            source,
        })?;
    let mut archive = ZipArchive::new(BufReader::new(file))
        .map_err(|error| CoreError::InvalidArchive(error.to_string()))?;

    let entry_count = u64::try_from(archive.len()).unwrap_or(u64::MAX);
    enforce_limit("entry_count", limits.max_entries, entry_count)?;

    let comment_bytes = archive.comment().to_vec();
    let archive_comment_was_lossy = std::str::from_utf8(&comment_bytes).is_err();
    let archive_comment =
        (!comment_bytes.is_empty()).then(|| String::from_utf8_lossy(&comment_bytes).into_owned());

    let mut entries = Vec::with_capacity(archive.len());
    let mut resources = Vec::new();
    let mut metadata_indices = HashMap::new();
    let mut collision_keys = BTreeSet::new();
    let mut file_collision_keys = HashSet::with_capacity(archive.len());
    let mut total_compressed_bytes = 0_u64;
    let mut total_uncompressed_bytes = 0_u64;
    let mut manifest_present = false;
    let mut signature_files = Vec::new();
    let mut signature_blocks = Vec::new();
    let mut warnings = Vec::new();
    let mut class_entries = Vec::new();
    let mut total_class_bytes = 0_u64;

    for index in 0..archive.len() {
        let entry = archive
            .by_index_raw(index)
            .map_err(|error| CoreError::InvalidArchive(error.to_string()))?;
        let original_name = entry.name().to_owned();
        let normalized_path = validate_archive_path(&original_name)?;
        let collision_key = normalized_path.trim_end_matches('/').to_lowercase();
        if collision_keys.contains(&collision_key) {
            return Err(CoreError::UnsafeArchivePath {
                entry: original_name,
                reason: "entry collides with another path under Windows normalization",
            });
        }
        let mut ancestor = String::new();
        let mut components = collision_key.split('/').peekable();
        while let Some(component) = components.next() {
            if components.peek().is_none() {
                break;
            }
            if !ancestor.is_empty() {
                ancestor.push('/');
            }
            ancestor.push_str(component);
            if file_collision_keys.contains(&ancestor) {
                return Err(CoreError::UnsafeArchivePath {
                    entry: original_name,
                    reason: "entry has a regular-file ancestor under Windows normalization",
                });
            }
        }
        if !entry.is_dir() {
            let descendant_prefix = format!("{collision_key}/");
            if collision_keys
                .range(descendant_prefix.clone()..)
                .next()
                .is_some_and(|existing: &String| existing.starts_with(&descendant_prefix))
            {
                return Err(CoreError::UnsafeArchivePath {
                    entry: original_name,
                    reason: "regular file conflicts with existing descendant entries",
                });
            }
            file_collision_keys.insert(collision_key.clone());
        }
        collision_keys.insert(collision_key);

        let unix_mode = entry.unix_mode();
        if unix_mode.is_some_and(is_symbolic_link) {
            return Err(CoreError::UnsafeArchivePath {
                entry: normalized_path,
                reason: "symbolic-link entries are forbidden",
            });
        }

        enforce_limit(
            "entry_uncompressed_bytes",
            limits.max_entry_uncompressed_bytes,
            entry.size(),
        )?;
        total_uncompressed_bytes = checked_total(
            "total_uncompressed_bytes",
            total_uncompressed_bytes,
            entry.size(),
        )?;
        enforce_limit(
            "total_uncompressed_bytes",
            limits.max_total_uncompressed_bytes,
            total_uncompressed_bytes,
        )?;
        total_compressed_bytes = checked_total(
            "total_compressed_bytes",
            total_compressed_bytes,
            entry.compressed_size(),
        )?;

        if let Some((kind, namespace, locale)) = classify_resource(&normalized_path, entry.is_dir())
        {
            resources.push(ResourceEntry {
                archive_index: u64::try_from(index).unwrap_or(u64::MAX),
                path: normalized_path.clone(),
                kind,
                namespace,
                locale,
                crc32: entry.crc32(),
                compressed_size_bytes: entry.compressed_size(),
                uncompressed_size_bytes: entry.size(),
            });
        }

        if !entry.is_dir() && normalized_path.ends_with(".class") {
            enforce_limit(
                "class_file_bytes",
                limits.max_class_file_bytes,
                entry.size(),
            )?;
            total_class_bytes =
                checked_total("total_class_scan_bytes", total_class_bytes, entry.size())?;
            enforce_limit(
                "total_class_scan_bytes",
                limits.max_total_class_scan_bytes,
                total_class_bytes,
            )?;
            class_entries.push(ClassArchiveEntry {
                index,
                path: normalized_path.clone(),
                declared_size: entry.size(),
            });
        }

        if DETECTION_PRECEDENCE
            .iter()
            .any(|(metadata_path, _, _)| normalized_path == *metadata_path)
        {
            enforce_limit(
                "loader_metadata_bytes",
                limits.max_loader_metadata_bytes,
                entry.size(),
            )?;
            metadata_indices.insert(normalized_path.clone(), index);
        }

        inspect_signature_path(
            &normalized_path,
            &mut manifest_present,
            &mut signature_files,
            &mut signature_blocks,
        );

        if entry.encrypted() {
            warnings.push(format!(
                "encrypted entry cannot be content-inspected: {normalized_path}"
            ));
        }
        let raw_name = entry.name_raw();
        let non_utf8_name_hex = std::str::from_utf8(raw_name)
            .is_err()
            .then(|| encode_hex(raw_name));
        if non_utf8_name_hex.is_some() {
            warnings.push(format!(
                "entry filename required legacy/non-UTF-8 decoding: {normalized_path}"
            ));
        }

        entries.push(ZipEntryRecord {
            index: u64::try_from(index).unwrap_or(u64::MAX),
            path: normalized_path,
            non_utf8_name_hex,
            entry_type: if entry.is_dir() {
                ZipEntryType::Directory
            } else {
                ZipEntryType::File
            },
            compression_method: format!("{:?}", entry.compression()),
            encrypted: entry.encrypted(),
            crc32: entry.crc32(),
            compressed_size_bytes: entry.compressed_size(),
            uncompressed_size_bytes: entry.size(),
            last_modified: entry.last_modified().map(|time| time.to_string()),
            unix_mode,
            comment: (!entry.comment().is_empty()).then(|| entry.comment().to_owned()),
            extra_data_size_bytes: entry
                .extra_data()
                .map_or(0, |data| u64::try_from(data.len()).unwrap_or(u64::MAX)),
            header_offset_bytes: entry.header_start(),
            data_offset_bytes: entry.data_start(),
        });
    }

    let parsed_metadata = read_metadata_sources(
        &mut archive,
        &metadata_indices,
        limits.max_loader_metadata_bytes,
    )?;
    let mod_metadata = summarize_metadata(path, parsed_metadata);
    for source in &mod_metadata.sources {
        if let Some(parse_error) = &source.parse_error {
            warnings.push(format!(
                "could not parse {} metadata at {}: {}",
                format_loader(source.loader),
                source.path,
                parse_error
            ));
        }
        if !source.rejected_mod_ids.is_empty() {
            warnings.push(format!(
                "rejected unsafe/invalid mod IDs from {}: {}",
                source.path,
                source.rejected_mod_ids.join(", ")
            ));
        }
    }
    if mod_metadata.used_filename_fallback {
        warnings.push(format!(
            "no usable loader modId was found; using sanitized JAR filename namespace {:?}",
            mod_metadata.primary_mod_id
        ));
    }
    if archive_comment_was_lossy {
        warnings.push("archive comment was not valid UTF-8 and was decoded lossily".to_owned());
    }

    let class_string_scan = scan_class_entries(
        &mut archive,
        &class_entries,
        total_class_bytes,
        limits,
        &mut warnings,
    )?;

    let signature_status = match (signature_files.is_empty(), signature_blocks.is_empty()) {
        (true, true) => SignatureEvidenceStatus::None,
        (false, false) => SignatureEvidenceStatus::PresentUnverified,
        _ => SignatureEvidenceStatus::IncompleteUnverified,
    };
    let modification_blocked_by_default = signature_status != SignatureEvidenceStatus::None;

    let canonical_path = fs::canonicalize(path).unwrap_or_else(|_| path.to_path_buf());
    Ok(ScanManifest {
        schema_version: MANIFEST_SCHEMA_VERSION,
        core_version: env!("CARGO_PKG_VERSION"),
        source: SourceArchive {
            path: canonical_path.to_string_lossy().into_owned(),
            file_name: path
                .file_name()
                .map_or_else(String::new, |name| name.to_string_lossy().into_owned()),
            size_bytes: source_metadata.len(),
        },
        applied_limits: limits,
        archive: ArchiveInventory {
            entry_count,
            total_compressed_bytes,
            total_uncompressed_bytes,
            archive_comment,
            archive_comment_was_lossy,
            entries,
            signatures: SignatureEvidence {
                status: signature_status,
                manifest_present,
                signature_files,
                signature_blocks,
                cryptographically_verified: false,
                modification_blocked_by_default,
                repack_warning: REPACK_WARNING,
            },
        },
        mod_metadata,
        resources,
        class_string_scan: Some(class_string_scan),
        warnings,
    })
}

/// Serialize a scan manifest as compact UTF-8 JSON.
pub fn scan_archive_json(path: impl AsRef<Path>) -> Result<String, CoreError> {
    let manifest = scan_archive(path)?;
    serde_json::to_string(&manifest).map_err(|error| CoreError::Serialization(error.to_string()))
}

fn read_metadata_sources<R: Read + std::io::Seek>(
    archive: &mut ZipArchive<R>,
    metadata_indices: &HashMap<String, usize>,
    read_limit: u64,
) -> Result<Vec<ParsedMetadata>, CoreError> {
    let mut parsed = Vec::new();
    for &(path, loader, format) in &DETECTION_PRECEDENCE {
        let Some(&index) = metadata_indices.get(path) else {
            continue;
        };
        let text = read_small_utf8_entry(archive, index, read_limit)?;
        let text_result = text.as_deref().map_err(String::as_str);
        parsed.push(parse_metadata(path, loader, format, text_result));
    }
    Ok(parsed)
}

fn read_small_utf8_entry<R: Read + std::io::Seek>(
    archive: &mut ZipArchive<R>,
    index: usize,
    read_limit: u64,
) -> Result<Result<String, String>, CoreError> {
    let entry = match archive.by_index(index) {
        Ok(entry) => entry,
        Err(error) => return Ok(Err(error.to_string())),
    };
    let mut bytes = Vec::with_capacity(usize::try_from(entry.size().min(read_limit)).unwrap_or(0));
    let mut bounded = entry.take(read_limit.saturating_add(1));
    if let Err(error) = bounded.read_to_end(&mut bytes) {
        return Ok(Err(error.to_string()));
    }
    if u64::try_from(bytes.len()).unwrap_or(u64::MAX) > read_limit {
        return Err(CoreError::LimitExceeded {
            kind: "loader_metadata_observed_bytes",
            limit: read_limit,
            actual: u64::try_from(bytes.len()).unwrap_or(u64::MAX),
        });
    }
    match String::from_utf8(bytes) {
        Ok(text) => Ok(Ok(text.trim_start_matches('\u{feff}').to_owned())),
        Err(error) => Ok(Err(format!("metadata is not UTF-8: {error}"))),
    }
}

fn scan_class_entries<R: Read + std::io::Seek>(
    archive: &mut ZipArchive<R>,
    class_entries: &[ClassArchiveEntry],
    total_class_bytes: u64,
    limits: ScanLimits,
    warnings: &mut Vec<String>,
) -> Result<ClassStringScan, CoreError> {
    let mut classes = Vec::with_capacity(class_entries.len());
    let mut references = Vec::new();
    let mut errors = Vec::new();
    let mut output_text_bytes = 0_u64;

    for class_entry in class_entries {
        let bytes = match read_class_entry(archive, class_entry.index, limits.max_class_file_bytes)?
        {
            Ok(bytes) => bytes,
            Err(error) => {
                record_class_error(class_entry, error, &mut errors, warnings);
                continue;
            }
        };
        if u64::try_from(bytes.len()).unwrap_or(u64::MAX) != class_entry.declared_size {
            record_class_error(
                class_entry,
                format!(
                    "declared class size {} differs from observed size {}",
                    class_entry.declared_size,
                    bytes.len()
                ),
                &mut errors,
                warnings,
            );
            continue;
        }

        match parse_classfile(
            &bytes,
            u64::try_from(class_entry.index).unwrap_or(u64::MAX),
            &class_entry.path,
        ) {
            Ok(parsed) => {
                let next_reference_count = checked_total(
                    "class_string_references",
                    u64::try_from(references.len()).unwrap_or(u64::MAX),
                    u64::try_from(parsed.references.len()).unwrap_or(u64::MAX),
                )?;
                enforce_limit(
                    "class_string_references",
                    limits.max_class_string_references,
                    next_reference_count,
                )?;
                for reference in &parsed.references {
                    let reference_bytes = reference_output_text_bytes(reference)?;
                    output_text_bytes = checked_total(
                        "class_string_output_bytes",
                        output_text_bytes,
                        reference_bytes,
                    )?;
                    enforce_limit(
                        "class_string_output_bytes",
                        limits.max_class_string_output_bytes,
                        output_text_bytes,
                    )?;
                }
                classes.push(parsed.summary);
                references.extend(parsed.references);
            }
            Err(error) => {
                record_class_error(class_entry, error.to_string(), &mut errors, warnings);
            }
        }
    }

    Ok(ClassStringScan {
        discovered_class_count: u64::try_from(class_entries.len()).unwrap_or(u64::MAX),
        successful_class_count: u64::try_from(classes.len()).unwrap_or(u64::MAX),
        failed_class_count: u64::try_from(errors.len()).unwrap_or(u64::MAX),
        total_class_bytes,
        classes,
        references,
        errors,
        mutation_policy: CLASS_MUTATION_POLICY,
    })
}

fn read_class_entry<R: Read + std::io::Seek>(
    archive: &mut ZipArchive<R>,
    index: usize,
    read_limit: u64,
) -> Result<Result<Vec<u8>, String>, CoreError> {
    let entry = match archive.by_index(index) {
        Ok(entry) => entry,
        Err(error) => return Ok(Err(error.to_string())),
    };
    let mut bytes = Vec::with_capacity(usize::try_from(entry.size().min(read_limit)).unwrap_or(0));
    let mut bounded = entry.take(read_limit.saturating_add(1));
    if let Err(error) = bounded.read_to_end(&mut bytes) {
        return Ok(Err(error.to_string()));
    }
    let observed = u64::try_from(bytes.len()).unwrap_or(u64::MAX);
    if observed > read_limit {
        return Err(CoreError::LimitExceeded {
            kind: "class_file_observed_bytes",
            limit: read_limit,
            actual: observed,
        });
    }
    Ok(Ok(bytes))
}

fn record_class_error(
    class_entry: &ClassArchiveEntry,
    error: String,
    errors: &mut Vec<ClassScanError>,
    warnings: &mut Vec<String>,
) {
    let bounded_error = truncate_diagnostic(error, 1_024);
    warnings.push(format!(
        "classfile scan skipped {}: {}",
        class_entry.path, bounded_error
    ));
    errors.push(ClassScanError {
        archive_index: u64::try_from(class_entry.index).unwrap_or(u64::MAX),
        archive_path: class_entry.path.clone(),
        error: bounded_error,
    });
}

fn reference_output_text_bytes(
    reference: &crate::model::ClassStringReference,
) -> Result<u64, CoreError> {
    [
        reference.archive_path.len(),
        reference.class.len(),
        reference.method.len(),
        reference.descriptor.len(),
        reference.value.len(),
    ]
    .into_iter()
    .try_fold(0_u64, |total, length| {
        checked_total(
            "class_string_output_bytes",
            total,
            u64::try_from(length).unwrap_or(u64::MAX),
        )
    })
}

fn truncate_diagnostic(mut value: String, max_bytes: usize) -> String {
    if value.len() <= max_bytes {
        return value;
    }
    let mut boundary = max_bytes;
    while !value.is_char_boundary(boundary) {
        boundary -= 1;
    }
    value.truncate(boundary);
    value.push_str("...[truncated]");
    value
}

fn is_valid_resource_namespace(value: &str) -> bool {
    !value.is_empty()
        && value.bytes().all(|byte| {
            byte.is_ascii_lowercase() || byte.is_ascii_digit() || matches!(byte, b'_' | b'-' | b'.')
        })
}

fn is_valid_locale_token(value: &str) -> bool {
    !value.is_empty()
        && value
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'_')
}

fn classify_resource(
    path: &str,
    is_directory: bool,
) -> Option<(ResourceKind, Option<String>, Option<String>)> {
    if is_directory {
        return None;
    }
    if path == "pack.txt" {
        return Some((ResourceKind::PackText, None, None));
    }
    if path.ends_with(".mcmeta") {
        return Some((ResourceKind::Mcmeta, None, None));
    }

    let parts: Vec<_> = path.split('/').collect();
    let (namespace, filename, shader_language) = match parts.as_slice() {
        ["assets", namespace, "lang", filename] => (*namespace, *filename, false),
        // OptiFine/Iris shader-pack option labels use /shaders/lang/<locale>.lang.
        // A synthetic namespace keeps this resource family isolated from normal assets.
        ["shaders", "lang", filename] => ("@shaderpack", *filename, true),
        _ => return None,
    };
    if !shader_language && !is_valid_resource_namespace(namespace) {
        return None;
    }
    let (locale, kind) = if !shader_language && let Some(locale) = filename.strip_suffix(".json") {
        (locale, ResourceKind::LanguageJson)
    } else {
        (filename.strip_suffix(".lang")?, ResourceKind::LanguageLang)
    };
    if !is_valid_locale_token(locale) {
        return None;
    }
    Some((kind, Some(namespace.to_owned()), Some(locale.to_owned())))
}

fn inspect_signature_path(
    path: &str,
    manifest_present: &mut bool,
    signature_files: &mut Vec<String>,
    signature_blocks: &mut Vec<String>,
) {
    let upper = path.to_ascii_uppercase();
    if upper == "META-INF/MANIFEST.MF" {
        *manifest_present = true;
        return;
    }
    let Some(file_name) = upper.strip_prefix("META-INF/") else {
        return;
    };
    if file_name.contains('/') {
        return;
    }
    if file_name.ends_with(".SF") {
        signature_files.push(path.to_owned());
    } else if file_name.ends_with(".RSA")
        || file_name.ends_with(".DSA")
        || file_name.ends_with(".EC")
    {
        signature_blocks.push(path.to_owned());
    }
}

const fn is_symbolic_link(mode: u32) -> bool {
    mode & 0o170_000 == 0o120_000
}

fn checked_total(kind: &'static str, current: u64, next: u64) -> Result<u64, CoreError> {
    current.checked_add(next).ok_or(CoreError::LimitExceeded {
        kind,
        limit: u64::MAX,
        actual: u64::MAX,
    })
}

fn enforce_limit(kind: &'static str, limit: u64, actual: u64) -> Result<(), CoreError> {
    if actual > limit {
        Err(CoreError::LimitExceeded {
            kind,
            limit,
            actual,
        })
    } else {
        Ok(())
    }
}

fn validate_limits(limits: ScanLimits) -> Result<(), CoreError> {
    if limits.max_archive_size_bytes == 0
        || limits.max_entries == 0
        || limits.max_entry_uncompressed_bytes == 0
        || limits.max_total_uncompressed_bytes == 0
        || limits.max_loader_metadata_bytes == 0
        || limits.max_class_file_bytes == 0
        || limits.max_total_class_scan_bytes == 0
        || limits.max_class_string_references == 0
        || limits.max_class_string_output_bytes == 0
    {
        return Err(CoreError::InvalidArgument(
            "all scan limits must be greater than zero".to_owned(),
        ));
    }
    Ok(())
}

fn preflight_zip_entry_count(
    file: &mut File,
    path: &Path,
    max_entries: u64,
) -> Result<(), CoreError> {
    const EOCD_MINIMUM_SIZE: u64 = 22;
    const MAX_ZIP_COMMENT_SIZE: u64 = 65_535;
    const ZIP64_LOCATOR_SIZE: usize = 20;
    const ZIP64_EOCD_MINIMUM_SIZE: usize = 56;
    const EOCD_SIGNATURE: &[u8; 4] = b"PK\x05\x06";
    const ZIP64_LOCATOR_SIGNATURE: &[u8; 4] = b"PK\x06\x07";
    const ZIP64_EOCD_SIGNATURE: &[u8; 4] = b"PK\x06\x06";

    let file_size = file
        .metadata()
        .map_err(|source| CoreError::Io {
            path: path.to_path_buf(),
            source,
        })?
        .len();
    if file_size < EOCD_MINIMUM_SIZE {
        return Err(CoreError::InvalidArchive(
            "archive is too short to contain an end-of-central-directory record".to_owned(),
        ));
    }

    let tail_size = file_size.min(EOCD_MINIMUM_SIZE + MAX_ZIP_COMMENT_SIZE);
    let tail_size_usize = usize::try_from(tail_size).map_err(|_| {
        CoreError::InvalidArchive("ZIP tail length does not fit address space".to_owned())
    })?;
    file.seek(SeekFrom::End(-i64::try_from(tail_size).unwrap_or(i64::MAX)))
        .map_err(|source| CoreError::Io {
            path: path.to_path_buf(),
            source,
        })?;
    let mut tail = vec![0_u8; tail_size_usize];
    file.read_exact(&mut tail).map_err(|source| CoreError::Io {
        path: path.to_path_buf(),
        source,
    })?;

    let eocd_offset = find_eocd(&tail, EOCD_SIGNATURE).ok_or_else(|| {
        CoreError::InvalidArchive("end-of-central-directory record was not found".to_owned())
    })?;
    let disk_number = read_le_u16(&tail, eocd_offset + 4)?;
    let central_directory_disk = read_le_u16(&tail, eocd_offset + 6)?;
    let entries_on_disk = read_le_u16(&tail, eocd_offset + 8)?;
    let entry_count_16 = read_le_u16(&tail, eocd_offset + 10)?;
    if disk_number != 0 || central_directory_disk != 0 || entries_on_disk != entry_count_16 {
        return Err(CoreError::InvalidArchive(
            "multi-disk ZIP archives are not supported".to_owned(),
        ));
    }

    let entry_count = if entry_count_16 != u16::MAX {
        u64::from(entry_count_16)
    } else {
        if eocd_offset < ZIP64_LOCATOR_SIZE {
            return Err(CoreError::InvalidArchive(
                "ZIP64 entry count is missing its locator".to_owned(),
            ));
        }
        let locator_offset = eocd_offset - ZIP64_LOCATOR_SIZE;
        if tail.get(locator_offset..locator_offset + 4) != Some(ZIP64_LOCATOR_SIGNATURE) {
            return Err(CoreError::InvalidArchive(
                "ZIP64 locator signature was not found".to_owned(),
            ));
        }
        let zip64_disk = read_le_u32(&tail, locator_offset + 4)?;
        let zip64_record_offset = read_le_u64(&tail, locator_offset + 8)?;
        let total_disks = read_le_u32(&tail, locator_offset + 16)?;
        if zip64_disk != 0 || total_disks != 1 {
            return Err(CoreError::InvalidArchive(
                "multi-disk ZIP64 archives are not supported".to_owned(),
            ));
        }
        let record_end = zip64_record_offset
            .checked_add(u64::try_from(ZIP64_EOCD_MINIMUM_SIZE).unwrap_or(u64::MAX))
            .ok_or_else(|| CoreError::InvalidArchive("ZIP64 offset overflow".to_owned()))?;
        if record_end > file_size {
            return Err(CoreError::InvalidArchive(
                "ZIP64 end-of-central-directory offset is outside the archive".to_owned(),
            ));
        }
        file.seek(SeekFrom::Start(zip64_record_offset))
            .map_err(|source| CoreError::Io {
                path: path.to_path_buf(),
                source,
            })?;
        let mut record = [0_u8; ZIP64_EOCD_MINIMUM_SIZE];
        file.read_exact(&mut record)
            .map_err(|source| CoreError::Io {
                path: path.to_path_buf(),
                source,
            })?;
        if record.get(0..4) != Some(ZIP64_EOCD_SIGNATURE) {
            return Err(CoreError::InvalidArchive(
                "ZIP64 end-of-central-directory signature was not found".to_owned(),
            ));
        }
        let record_size = read_le_u64(&record, 4)?;
        if record_size < 44 {
            return Err(CoreError::InvalidArchive(
                "ZIP64 end-of-central-directory record is too short".to_owned(),
            ));
        }
        let disk = read_le_u32(&record, 16)?;
        let central_disk = read_le_u32(&record, 20)?;
        let entries_on_disk_64 = read_le_u64(&record, 24)?;
        let total_entries_64 = read_le_u64(&record, 32)?;
        if disk != 0 || central_disk != 0 || entries_on_disk_64 != total_entries_64 {
            return Err(CoreError::InvalidArchive(
                "multi-disk ZIP64 archives are not supported".to_owned(),
            ));
        }
        total_entries_64
    };

    enforce_limit("entry_count", max_entries, entry_count)
}

fn find_eocd(tail: &[u8], signature: &[u8; 4]) -> Option<usize> {
    if tail.len() < 22 {
        return None;
    }
    for offset in (0..=tail.len() - 22).rev() {
        if tail.get(offset..offset + 4) != Some(signature) {
            continue;
        }
        let comment_length =
            usize::from(u16::from_le_bytes([tail[offset + 20], tail[offset + 21]]));
        if offset
            .checked_add(22)
            .and_then(|end| end.checked_add(comment_length))
            == Some(tail.len())
        {
            return Some(offset);
        }
    }
    None
}

fn read_le_u16(bytes: &[u8], offset: usize) -> Result<u16, CoreError> {
    let data = bytes
        .get(offset..offset.saturating_add(2))
        .ok_or_else(|| CoreError::InvalidArchive("truncated ZIP metadata".to_owned()))?;
    Ok(u16::from_le_bytes([data[0], data[1]]))
}

fn read_le_u32(bytes: &[u8], offset: usize) -> Result<u32, CoreError> {
    let data = bytes
        .get(offset..offset.saturating_add(4))
        .ok_or_else(|| CoreError::InvalidArchive("truncated ZIP metadata".to_owned()))?;
    Ok(u32::from_le_bytes([data[0], data[1], data[2], data[3]]))
}

fn read_le_u64(bytes: &[u8], offset: usize) -> Result<u64, CoreError> {
    let data = bytes
        .get(offset..offset.saturating_add(8))
        .ok_or_else(|| CoreError::InvalidArchive("truncated ZIP metadata".to_owned()))?;
    Ok(u64::from_le_bytes([
        data[0], data[1], data[2], data[3], data[4], data[5], data[6], data[7],
    ]))
}

fn encode_hex(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut output = String::with_capacity(bytes.len().saturating_mul(2));
    for byte in bytes {
        output.push(char::from(HEX[usize::from(byte >> 4)]));
        output.push(char::from(HEX[usize::from(byte & 0x0f)]));
    }
    output
}

const fn format_loader(loader: crate::model::ModLoader) -> &'static str {
    match loader {
        crate::model::ModLoader::Fabric => "Fabric",
        crate::model::ModLoader::Forge => "Forge",
        crate::model::ModLoader::NeoForge => "NeoForge",
        crate::model::ModLoader::Quilt => "Quilt",
    }
}

#[cfg(test)]
mod tests {
    use super::{classify_resource, find_eocd, inspect_signature_path};
    use crate::model::ResourceKind;

    #[test]
    fn recognizes_requested_resource_paths() {
        let (kind, namespace, locale) =
            classify_resource("assets/example/lang/zh_cn.json", false).unwrap();
        assert_eq!(kind, ResourceKind::LanguageJson);
        assert_eq!(namespace.as_deref(), Some("example"));
        assert_eq!(locale.as_deref(), Some("zh_cn"));

        assert_eq!(
            classify_resource("assets/example/lang/en_us.lang", false)
                .unwrap()
                .0,
            ResourceKind::LanguageLang
        );
        assert_eq!(
            classify_resource("pack.txt", false).unwrap().0,
            ResourceKind::PackText
        );
        assert_eq!(
            classify_resource("nested/example.mcmeta", false).unwrap().0,
            ResourceKind::Mcmeta
        );
        assert!(classify_resource("assets/example/text/en_us.json", false).is_none());
        assert!(classify_resource("assets/example/lang/nested/en_us.json", false).is_none());
        let (shader_kind, shader_namespace, shader_locale) =
            classify_resource("shaders/lang/fr_fr.lang", false).unwrap();
        assert_eq!(shader_kind, ResourceKind::LanguageLang);
        assert_eq!(shader_namespace.as_deref(), Some("@shaderpack"));
        assert_eq!(shader_locale.as_deref(), Some("fr_fr"));
        assert!(classify_resource("shaders/lang/en_us.json", false).is_none());
        assert!(classify_resource("assets/example/lang/en_us.old.json", false).is_none());
        assert!(classify_resource("assets/@shaderpack/lang/en_us.json", false).is_none());
        assert!(classify_resource("assets/Example/lang/en_us.json", false).is_none());
        assert!(classify_resource("shaders/lang/EN_US.lang", false).is_none());
    }

    #[test]
    fn signature_detection_is_case_insensitive_but_direct_child_only() {
        let mut manifest = false;
        let mut files = Vec::new();
        let mut blocks = Vec::new();
        for path in [
            "meta-inf/manifest.mf",
            "META-INF/CERT.SF",
            "META-INF/CERT.RSA",
            "META-INF/nested/NOPE.SF",
        ] {
            inspect_signature_path(path, &mut manifest, &mut files, &mut blocks);
        }
        assert!(manifest);
        assert_eq!(files, ["META-INF/CERT.SF"]);
        assert_eq!(blocks, ["META-INF/CERT.RSA"]);
    }

    #[test]
    fn eocd_search_ignores_signature_bytes_inside_the_comment() {
        let comment = b"comment PK\x05\x06 inside";
        let mut eocd = Vec::new();
        eocd.extend_from_slice(b"PK\x05\x06");
        eocd.extend_from_slice(&[0; 16]);
        eocd.extend_from_slice(&u16::try_from(comment.len()).unwrap().to_le_bytes());
        eocd.extend_from_slice(comment);
        assert_eq!(find_eocd(&eocd, b"PK\x05\x06"), Some(0));

        eocd.push(0);
        assert_eq!(find_eocd(&eocd, b"PK\x05\x06"), None);
    }
}
