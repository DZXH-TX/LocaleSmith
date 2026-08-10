use std::{
    ffi::{CStr, CString},
    fs::{self, File},
    io::Write,
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
    time::{SystemTime, UNIX_EPOCH},
};

use localesmith_core::{
    CoreError, ErrorCode, ModLoader, ResourceKind, ScanLimits, SignatureEvidenceStatus,
    ffi::{localesmith_last_error_code, localesmith_scan_archive_json, localesmith_string_free},
    scan_archive, scan_archive_with_limits,
};
use zip::{
    CompressionMethod, ZipWriter,
    write::{ExtendedFileOptions, FileOptions, SimpleFileOptions},
};

static TEST_COUNTER: AtomicU64 = AtomicU64::new(0);

struct TestArchive {
    directory: PathBuf,
    path: PathBuf,
}

impl TestArchive {
    fn new(file_name: &str) -> Self {
        let counter = TEST_COUNTER.fetch_add(1, Ordering::Relaxed);
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let directory = std::env::temp_dir().join(format!(
            "localesmith-core-test-{}-{counter}-{nanos}",
            std::process::id()
        ));
        fs::create_dir_all(&directory).unwrap();
        let path = directory.join(file_name);
        Self { directory, path }
    }

    fn write_entries(&self, entries: &[(&str, &[u8])], comment: Option<&str>) {
        let file = File::create(&self.path).unwrap();
        let mut writer = ZipWriter::new(file);
        let options = SimpleFileOptions::default()
            .compression_method(CompressionMethod::Deflated)
            .unix_permissions(0o644);
        if let Some(comment) = comment {
            writer.set_comment(comment);
        }
        for (name, contents) in entries {
            writer.start_file(*name, options).unwrap();
            writer.write_all(contents).unwrap();
        }
        writer.finish().unwrap();
    }

    fn write_symlink(&self, name: &str, target: &str) {
        let file = File::create(&self.path).unwrap();
        let mut writer = ZipWriter::new(file);
        let options: FileOptions<'_, ExtendedFileOptions> = FileOptions::default();
        writer.add_symlink(name, target, options).unwrap();
        writer.finish().unwrap();
    }
}

impl Drop for TestArchive {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.directory);
    }
}

#[test]
fn scans_fabric_resources_zip_metadata_and_signature_evidence() {
    let archive = TestArchive::new("Fabric Demo.jar");
    archive.write_entries(
        &[
            (
                "fabric.mod.json",
                br#"{"schemaVersion":1,"id":"fabric_demo","version":"1.0"}"#,
            ),
            (
                "assets/fabric_demo/lang/en_us.json",
                br#"{"item.fabric_demo.sample":"Sample"}"#,
            ),
            (
                "assets/fabric_demo/lang/zh_cn.lang",
                b"item.fabric_demo.sample=Sample",
            ),
            ("pack.txt", b"Resource pack description"),
            ("pack.mcmeta", br#"{"pack":{"pack_format":15}}"#),
            ("META-INF/MANIFEST.MF", b"Manifest-Version: 1.0\r\n"),
            ("META-INF/DEMO.SF", b"Signature-Version: 1.0\r\n"),
            ("META-INF/DEMO.RSA", b"not-a-real-signature"),
            ("example/Demo.class", b"class bytes"),
        ],
        Some("scan fixture"),
    );

    let manifest = scan_archive(&archive.path).unwrap();
    assert_eq!(manifest.schema_version, 1);
    assert_eq!(
        manifest.mod_metadata.primary_loader,
        Some(ModLoader::Fabric)
    );
    assert_eq!(manifest.mod_metadata.primary_mod_id, "fabric_demo");
    assert!(!manifest.mod_metadata.used_filename_fallback);
    assert_eq!(manifest.archive.entry_count, 9);
    assert_eq!(
        manifest.archive.archive_comment.as_deref(),
        Some("scan fixture")
    );
    assert_eq!(
        manifest.archive.signatures.status,
        SignatureEvidenceStatus::PresentUnverified
    );
    assert!(!manifest.archive.signatures.cryptographically_verified);
    assert!(manifest.archive.signatures.modification_blocked_by_default);
    assert!(
        manifest
            .archive
            .signatures
            .repack_warning
            .contains("invalidates")
    );

    assert_eq!(manifest.resources.len(), 4);
    assert!(
        manifest
            .resources
            .iter()
            .any(|resource| resource.kind == ResourceKind::LanguageJson
                && resource.namespace.as_deref() == Some("fabric_demo")
                && resource.locale.as_deref() == Some("en_us"))
    );
    assert!(
        manifest
            .resources
            .iter()
            .any(|resource| resource.kind == ResourceKind::LanguageLang)
    );
    assert!(
        manifest
            .archive
            .entries
            .iter()
            .all(|entry| !entry.path.starts_with('/') && !entry.path.contains(".."))
    );
}

#[test]
fn detects_neoforge_forge_quilt_and_legacy_forge_metadata() {
    let cases: &[(&str, &str, &[u8], ModLoader, &str)] = &[
        (
            "neo.jar",
            "META-INF/neoforge.mods.toml",
            b"modLoader='javafml'\n[[mods]]\nmodId='neo_demo'",
            ModLoader::NeoForge,
            "neo_demo",
        ),
        (
            "forge.jar",
            "META-INF/mods.toml",
            b"modLoader='javafml'\n[[mods]]\nmodId='forge_demo'",
            ModLoader::Forge,
            "forge_demo",
        ),
        (
            "quilt.jar",
            "quilt.mod.json",
            br#"{"quilt_loader":{"id":"quilt_demo","version":"1"}}"#,
            ModLoader::Quilt,
            "quilt_demo",
        ),
        (
            "legacy.jar",
            "mcmod.info",
            br#"[{"modid":"legacy_demo","name":"Legacy Demo"}]"#,
            ModLoader::Forge,
            "legacy_demo",
        ),
    ];

    for &(file_name, metadata_path, metadata, expected_loader, expected_id) in cases {
        let archive = TestArchive::new(file_name);
        archive.write_entries(&[(metadata_path, metadata)], None);
        let manifest = scan_archive(&archive.path).unwrap();
        assert_eq!(manifest.mod_metadata.primary_loader, Some(expected_loader));
        assert_eq!(manifest.mod_metadata.primary_mod_id, expected_id);
    }
}

#[test]
fn neoforge_metadata_has_deterministic_precedence() {
    let archive = TestArchive::new("mixed.jar");
    archive.write_entries(
        &[
            (
                "META-INF/neoforge.mods.toml",
                b"[[mods]]\nmodId='neo_primary'",
            ),
            ("fabric.mod.json", br#"{"id":"fabric_secondary"}"#),
        ],
        None,
    );
    let manifest = scan_archive(&archive.path).unwrap();
    assert_eq!(
        manifest.mod_metadata.primary_loader,
        Some(ModLoader::NeoForge)
    );
    assert_eq!(manifest.mod_metadata.primary_mod_id, "neo_primary");
    assert_eq!(
        manifest.mod_metadata.mod_ids,
        ["neo_primary", "fabric_secondary"]
    );
}

#[test]
fn falls_back_to_sanitized_jar_filename_when_metadata_is_absent_or_bad() {
    let absent = TestArchive::new("My Great Mod 2.5.jar");
    absent.write_entries(&[("example/Class.class", b"class")], None);
    let manifest = scan_archive(&absent.path).unwrap();
    assert!(manifest.mod_metadata.used_filename_fallback);
    assert_eq!(manifest.mod_metadata.primary_mod_id, "my_great_mod_2.5");

    let malformed = TestArchive::new("Fallback Name.jar");
    malformed.write_entries(&[("fabric.mod.json", b"not json")], None);
    let manifest = scan_archive(&malformed.path).unwrap();
    assert!(manifest.mod_metadata.used_filename_fallback);
    assert_eq!(manifest.mod_metadata.primary_mod_id, "fallback_name");
    assert!(
        manifest
            .warnings
            .iter()
            .any(|warning| warning.contains("could not parse Fabric"))
    );
}

#[test]
fn rejects_zip_slip_windows_collisions_and_symlinks() {
    let traversal = TestArchive::new("traversal.jar");
    traversal.write_entries(&[("..\\outside.txt", b"bad")], None);
    assert!(matches!(
        scan_archive(&traversal.path),
        Err(CoreError::UnsafeArchivePath { .. })
    ));

    let collision = TestArchive::new("collision.jar");
    collision.write_entries(
        &[("assets/demo/A.txt", b"one"), ("ASSETS/demo/a.txt", b"two")],
        None,
    );
    assert!(matches!(
        scan_archive(&collision.path),
        Err(CoreError::UnsafeArchivePath { .. })
    ));

    let unicode_collision = TestArchive::new("unicode-collision.jar");
    unicode_collision.write_entries(&[("Ä.txt", b"one"), ("ä.txt", b"two")], None);
    assert!(matches!(
        scan_archive(&unicode_collision.path),
        Err(CoreError::UnsafeArchivePath { .. })
    ));

    for (file_name, entries) in [
        (
            "ancestor-first.jar",
            [("parent", b"file".as_slice()), ("parent/child", b"child")],
        ),
        (
            "descendant-first.jar",
            [("parent/child", b"child".as_slice()), ("parent", b"file")],
        ),
    ] {
        let hierarchy_collision = TestArchive::new(file_name);
        hierarchy_collision.write_entries(&entries, None);
        assert!(matches!(
            scan_archive(&hierarchy_collision.path),
            Err(CoreError::UnsafeArchivePath { .. })
        ));
    }

    let symlink = TestArchive::new("symlink.jar");
    symlink.write_symlink("assets/demo/link", "../../outside");
    assert!(matches!(
        scan_archive(&symlink.path),
        Err(CoreError::UnsafeArchivePath { .. })
    ));
}

#[test]
fn enforces_archive_entry_and_uncompressed_size_limits() {
    let archive = TestArchive::new("limits.jar");
    archive.write_entries(&[("one.txt", b"1234"), ("two.txt", b"5678")], None);

    let entry_count_limits = ScanLimits {
        max_entries: 1,
        ..ScanLimits::default()
    };
    assert_limit_error(scan_archive_with_limits(&archive.path, entry_count_limits));

    let entry_size_limits = ScanLimits {
        max_entry_uncompressed_bytes: 3,
        ..ScanLimits::default()
    };
    assert_limit_error(scan_archive_with_limits(&archive.path, entry_size_limits));

    let total_size_limits = ScanLimits {
        max_total_uncompressed_bytes: 7,
        ..ScanLimits::default()
    };
    assert_limit_error(scan_archive_with_limits(&archive.path, total_size_limits));

    let archive_size_limits = ScanLimits {
        max_archive_size_bytes: 1,
        ..ScanLimits::default()
    };
    assert_limit_error(scan_archive_with_limits(&archive.path, archive_size_limits));
}

#[test]
fn invalid_zip_is_reported_without_panicking() {
    let archive = TestArchive::new("not-a-zip.jar");
    fs::write(&archive.path, b"this is not a zip file").unwrap();
    assert!(matches!(
        scan_archive(&archive.path),
        Err(CoreError::InvalidArchive(_))
    ));
}

#[test]
fn scans_classfile_ldc_references_and_records_malformed_classes() {
    let archive = TestArchive::new("classes.jar");
    let valid_class = fixture_class(&[0x12, 11, 0xb0], &[0x13, 0x00, 13, 0xb0]);
    archive.write_entries(
        &[
            ("example/Test.class", &valid_class),
            ("example/Bad.class", b"not a classfile"),
        ],
        None,
    );

    let manifest = scan_archive(&archive.path).unwrap();
    let class_scan = manifest.class_string_scan.unwrap();
    assert_eq!(class_scan.discovered_class_count, 2);
    assert_eq!(class_scan.successful_class_count, 1);
    assert_eq!(class_scan.failed_class_count, 1);
    assert_eq!(class_scan.classes[0].class, "example/Test");
    assert_eq!(class_scan.references.len(), 2);
    assert_eq!(class_scan.references[0].method, "message");
    assert_eq!(class_scan.references[0].descriptor, "()Ljava/lang/String;");
    assert_eq!(class_scan.references[0].bytecode_offset, 0);
    assert_eq!(class_scan.references[0].value, "Hello player!");
    assert_eq!(class_scan.references[0].constant_pool_index, 11);
    assert!(class_scan.references[0].candidate);
    assert_eq!(class_scan.references[1].opcode, "ldc_w");
    assert!(!class_scan.references[1].candidate);
    assert_eq!(
        class_scan.references[1].rejected_reason,
        Some("likely_identifier_or_translation_key")
    );
    assert_eq!(class_scan.errors[0].archive_path, "example/Bad.class");
    assert!(
        manifest
            .warnings
            .iter()
            .any(|warning| warning.contains("example/Bad.class"))
    );
    assert!(class_scan.mutation_policy.contains("never rewritten"));
}

#[test]
fn enforces_per_class_total_class_and_candidate_output_limits() {
    let archive = TestArchive::new("class-limits.jar");
    let valid_class = fixture_class(&[0x12, 11, 0xb0], &[0x13, 0x00, 13, 0xb0]);
    archive.write_entries(
        &[
            ("example/One.class", &valid_class),
            ("example/Two.class", &valid_class),
        ],
        None,
    );

    let per_class = ScanLimits {
        max_class_file_bytes: u64::try_from(valid_class.len() - 1).unwrap(),
        ..ScanLimits::default()
    };
    assert_limit_error(scan_archive_with_limits(&archive.path, per_class));

    let total_class = ScanLimits {
        max_total_class_scan_bytes: u64::try_from(valid_class.len()).unwrap(),
        ..ScanLimits::default()
    };
    assert_limit_error(scan_archive_with_limits(&archive.path, total_class));

    let reference_count = ScanLimits {
        max_class_string_references: 1,
        ..ScanLimits::default()
    };
    assert_limit_error(scan_archive_with_limits(&archive.path, reference_count));

    let output_bytes = ScanLimits {
        max_class_string_output_bytes: 1,
        ..ScanLimits::default()
    };
    assert_limit_error(scan_archive_with_limits(&archive.path, output_bytes));
}

#[test]
fn c_abi_returns_owned_json_and_reports_errors() {
    let archive = TestArchive::new("ffi.jar");
    archive.write_entries(&[("fabric.mod.json", br#"{"id":"ffi_demo"}"#)], None);
    let path = CString::new(path_to_utf8(&archive.path)).unwrap();
    let mut output = std::ptr::null_mut();

    // SAFETY: `path` is a live NUL-terminated string and `output` is writable
    // pointer storage. The returned allocation is freed once below.
    let code = unsafe { localesmith_scan_archive_json(path.as_ptr(), &mut output) };
    assert_eq!(code, ErrorCode::Ok as i32);
    assert_eq!(localesmith_last_error_code(), ErrorCode::Ok as i32);
    assert!(!output.is_null());
    // SAFETY: On success the ABI guarantees a live NUL-terminated allocation.
    let json = unsafe { CStr::from_ptr(output) }.to_str().unwrap();
    let value: serde_json::Value = serde_json::from_str(json).unwrap();
    assert_eq!(value["schema_version"], 1);
    assert_eq!(value["mod_metadata"]["primary_mod_id"], "ffi_demo");
    // SAFETY: The allocation was returned by this library and is freed once.
    unsafe { localesmith_string_free(output) };

    let missing = CString::new(path_to_utf8(&archive.directory.join("missing.jar"))).unwrap();
    let mut failed_output = std::ptr::null_mut();
    // SAFETY: Both pointers satisfy the ABI storage/lifetime contract.
    let code = unsafe { localesmith_scan_archive_json(missing.as_ptr(), &mut failed_output) };
    assert_eq!(code, ErrorCode::Io as i32);
    assert!(failed_output.is_null());
}

fn assert_limit_error(result: Result<localesmith_core::ScanManifest, CoreError>) {
    assert!(matches!(result, Err(CoreError::LimitExceeded { .. })));
}

fn path_to_utf8(path: &Path) -> String {
    path.to_str()
        .expect("test temp path must be UTF-8")
        .to_owned()
}

fn fixture_class(first_code: &[u8], second_code: &[u8]) -> Vec<u8> {
    let mut bytes = Vec::new();
    push_u32(&mut bytes, 0xcafe_babe);
    push_u16(&mut bytes, 0);
    push_u16(&mut bytes, 61);
    push_u16(&mut bytes, 15);
    push_utf8(&mut bytes, "example/Test"); // #1
    push_class(&mut bytes, 1); // #2
    push_utf8(&mut bytes, "java/lang/Object"); // #3
    push_class(&mut bytes, 3); // #4
    push_utf8(&mut bytes, "message"); // #5
    push_utf8(&mut bytes, "()Ljava/lang/String;"); // #6
    push_utf8(&mut bytes, "Code"); // #7
    bytes.push(5); // #8 long, #9 reserved double slot
    bytes.extend_from_slice(&0_u64.to_be_bytes());
    push_utf8(&mut bytes, "Hello player!"); // #10
    push_string(&mut bytes, 10); // #11
    push_utf8(&mut bytes, "example.translation.key"); // #12
    push_string(&mut bytes, 12); // #13
    push_utf8(&mut bytes, "keyMessage"); // #14

    push_u16(&mut bytes, 0x0021);
    push_u16(&mut bytes, 2);
    push_u16(&mut bytes, 4);
    push_u16(&mut bytes, 0); // interfaces
    push_u16(&mut bytes, 0); // fields
    push_u16(&mut bytes, 2); // methods
    push_method(&mut bytes, 5, 6, first_code);
    push_method(&mut bytes, 14, 6, second_code);
    push_u16(&mut bytes, 0); // class attributes
    bytes
}

fn push_utf8(output: &mut Vec<u8>, value: &str) {
    output.push(1);
    push_u16(output, u16::try_from(value.len()).unwrap());
    output.extend_from_slice(value.as_bytes());
}

fn push_class(output: &mut Vec<u8>, name_index: u16) {
    output.push(7);
    push_u16(output, name_index);
}

fn push_string(output: &mut Vec<u8>, value_index: u16) {
    output.push(8);
    push_u16(output, value_index);
}

fn push_method(output: &mut Vec<u8>, name: u16, descriptor: u16, code: &[u8]) {
    push_u16(output, 0x0009);
    push_u16(output, name);
    push_u16(output, descriptor);
    push_u16(output, 1);
    push_u16(output, 7); // Code attribute name
    push_u32(output, u32::try_from(12 + code.len()).unwrap());
    push_u16(output, 1);
    push_u16(output, 0);
    push_u32(output, u32::try_from(code.len()).unwrap());
    output.extend_from_slice(code);
    push_u16(output, 0);
    push_u16(output, 0);
}

fn push_u16(output: &mut Vec<u8>, value: u16) {
    output.extend_from_slice(&value.to_be_bytes());
}

fn push_u32(output: &mut Vec<u8>, value: u32) {
    output.extend_from_slice(&value.to_be_bytes());
}
