use std::path::{Component, Path, PathBuf};

use crate::error::CoreError;

/// Validate and normalize a ZIP entry path without touching the filesystem.
///
/// Backslashes are treated as separators so a path that is harmless on Unix but
/// traverses on Windows is rejected. The returned path always uses `/`.
pub(crate) fn validate_archive_path(entry: &str) -> Result<String, CoreError> {
    if entry.is_empty() {
        return Err(unsafe_path(entry, "entry name is empty"));
    }
    if entry.contains('\0') {
        return Err(unsafe_path(entry, "entry name contains NUL"));
    }
    if entry.starts_with('/') || entry.starts_with('\\') {
        return Err(unsafe_path(entry, "absolute paths are forbidden"));
    }

    let normalized = entry.replace('\\', "/");
    if normalized.encode_utf16().count() > 32_000 {
        return Err(unsafe_path(entry, "entry path is too long for Windows"));
    }
    let without_directory_suffix = normalized.strip_suffix('/').unwrap_or(&normalized);
    if without_directory_suffix.is_empty() {
        return Err(unsafe_path(entry, "archive root is not a valid entry"));
    }

    for component in without_directory_suffix.split('/') {
        if component.is_empty() {
            return Err(unsafe_path(entry, "empty path components are forbidden"));
        }
        if matches!(component, "." | "..") {
            return Err(unsafe_path(entry, "dot path components are forbidden"));
        }
        if component.contains(':') {
            return Err(unsafe_path(
                entry,
                "drive prefixes and NTFS alternate data streams are forbidden",
            ));
        }
        if component.chars().any(|character| {
            character <= '\u{1f}' || matches!(character, '<' | '>' | '"' | '|' | '?' | '*')
        }) {
            return Err(unsafe_path(
                entry,
                "entry contains characters forbidden in Windows filenames",
            ));
        }
        if component.encode_utf16().count() > 255 {
            return Err(unsafe_path(
                entry,
                "path component is too long for Windows filesystems",
            ));
        }
        if component.ends_with(' ') || component.ends_with('.') {
            return Err(unsafe_path(
                entry,
                "Windows-normalized trailing spaces or dots are forbidden",
            ));
        }
        if is_windows_reserved_name(component) {
            return Err(unsafe_path(entry, "Windows device names are forbidden"));
        }
    }

    // This is a second, platform-independent guard. It documents the invariant
    // expected by any future extractor even when this code is built off Windows.
    let path = Path::new(without_directory_suffix);
    if path.is_absolute()
        || path
            .components()
            .any(|part| !matches!(part, Component::Normal(_)))
    {
        return Err(unsafe_path(entry, "path is not a relative normal path"));
    }

    Ok(normalized)
}

pub(crate) fn sanitize_jar_namespace(path: &Path) -> String {
    let stem = path
        .file_stem()
        .and_then(|value| value.to_str())
        .filter(|value| !value.is_empty())
        .unwrap_or("mod");

    let mut sanitized = String::with_capacity(stem.len().min(64));
    let mut previous_was_separator = false;
    for character in stem.chars() {
        let mapped = if character.is_ascii_alphanumeric() {
            Some(character.to_ascii_lowercase())
        } else if matches!(character, '_' | '-' | '.') {
            Some(character)
        } else {
            Some('_')
        };

        if let Some(mapped) = mapped {
            let is_separator = matches!(mapped, '_' | '-' | '.');
            if is_separator && previous_was_separator {
                continue;
            }
            sanitized.push(mapped);
            previous_was_separator = is_separator;
        }
        if sanitized.len() >= 64 {
            break;
        }
    }

    let sanitized = sanitized.trim_matches(['_', '-', '.']);
    if sanitized.is_empty() {
        format!("mod_{:016x}", fnv1a64(stem.as_bytes()))
    } else if sanitized.as_bytes().first().is_some_and(u8::is_ascii_digit) {
        format!("mod_{sanitized}")
    } else {
        sanitized.to_owned()
    }
}

pub(crate) fn is_safe_mod_id(value: &str) -> bool {
    let mut bytes = value.bytes();
    let Some(first) = bytes.next() else {
        return false;
    };
    if !first.is_ascii_lowercase() {
        return false;
    }
    value.len() <= 128
        && bytes.all(|byte| {
            byte.is_ascii_lowercase() || byte.is_ascii_digit() || matches!(byte, b'_' | b'-' | b'.')
        })
}

fn unsafe_path(entry: &str, reason: &'static str) -> CoreError {
    CoreError::UnsafeArchivePath {
        entry: entry.to_owned(),
        reason,
    }
}

fn is_windows_reserved_name(component: &str) -> bool {
    let base = component
        .split_once('.')
        .map_or(component, |(before_extension, _)| before_extension)
        .trim_end_matches([' ', '.'])
        .replace('¹', "1")
        .replace('²', "2")
        .replace('³', "3")
        .to_ascii_uppercase();
    matches!(
        base.as_str(),
        "CON"
            | "PRN"
            | "AUX"
            | "NUL"
            | "CLOCK$"
            | "CONIN$"
            | "CONOUT$"
            | "COM1"
            | "COM2"
            | "COM3"
            | "COM4"
            | "COM5"
            | "COM6"
            | "COM7"
            | "COM8"
            | "COM9"
            | "LPT1"
            | "LPT2"
            | "LPT3"
            | "LPT4"
            | "LPT5"
            | "LPT6"
            | "LPT7"
            | "LPT8"
            | "LPT9"
    )
}

const fn fnv1a64(bytes: &[u8]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    let mut index = 0;
    while index < bytes.len() {
        hash ^= bytes[index] as u64;
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
        index += 1;
    }
    hash
}

#[allow(dead_code)]
pub(crate) fn checked_child(root: &Path, entry: &str) -> Result<PathBuf, CoreError> {
    let normalized = validate_archive_path(entry)?;
    Ok(normalized
        .split('/')
        .fold(root.to_path_buf(), |path, part| path.join(part)))
}

#[cfg(test)]
mod tests {
    use super::{is_safe_mod_id, sanitize_jar_namespace, validate_archive_path};
    use std::path::Path;

    #[test]
    fn accepts_normal_relative_archive_paths() {
        assert_eq!(
            validate_archive_path("assets/example/lang/en_us.json").unwrap(),
            "assets/example/lang/en_us.json"
        );
        assert_eq!(
            validate_archive_path("assets\\example\\lang\\en_us.json").unwrap(),
            "assets/example/lang/en_us.json"
        );
        assert_eq!(validate_archive_path("META-INF/").unwrap(), "META-INF/");
    }

    #[test]
    fn rejects_windows_and_posix_traversal_forms() {
        for path in [
            "../evil",
            "a/../../evil",
            "a\\..\\evil",
            "/absolute",
            "\\absolute",
            "C:/Windows/file",
            "safe/file:stream",
            "a//b",
            "./a",
            "folder/NUL.txt",
            "folder/COM¹.txt",
            "folder/CONIN$",
            "folder/bad?.txt",
            "folder/control\u{1f}.txt",
            "folder/trailing. /file",
        ] {
            assert!(validate_archive_path(path).is_err(), "accepted {path:?}");
        }
    }

    #[test]
    fn sanitizes_fallback_namespace_deterministically() {
        assert_eq!(
            sanitize_jar_namespace(Path::new("My Mod 1.2.3.jar")),
            "my_mod_1.2.3"
        );
        assert_eq!(
            sanitize_jar_namespace(Path::new("123 demo.jar")),
            "mod_123_demo"
        );
        assert!(sanitize_jar_namespace(Path::new("模组.jar")).starts_with("mod_"));
    }

    #[test]
    fn validates_safe_mod_ids() {
        assert!(is_safe_mod_id("example_mod"));
        assert!(is_safe_mod_id("example-mod.api"));
        assert!(!is_safe_mod_id("ExampleMod"));
        assert!(!is_safe_mod_id("../example"));
        assert!(!is_safe_mod_id("1example"));
    }
}
