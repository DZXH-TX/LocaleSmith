use std::path::PathBuf;

use thiserror::Error;

/// Stable numeric error codes shared with C/C# callers.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum ErrorCode {
    /// The operation completed successfully.
    Ok = 0,
    /// A required pointer was null.
    NullPointer = 1,
    /// A C string was not valid UTF-8.
    InvalidUtf8 = 2,
    /// A filesystem operation failed.
    Io = 3,
    /// The input was not a valid or supported ZIP archive.
    InvalidArchive = 4,
    /// A configured resource limit was exceeded.
    LimitExceeded = 5,
    /// An archive entry was unsafe for extraction on Windows.
    UnsafeArchivePath = 6,
    /// JSON serialization failed.
    Serialization = 7,
    /// A panic was caught at the FFI boundary.
    Panic = 8,
    /// A non-pointer argument was invalid.
    InvalidArgument = 9,
}

/// Errors produced by the safe Rust API.
#[derive(Debug, Error)]
pub enum CoreError {
    /// Filesystem access failed.
    #[error("I/O error for {path}: {source}")]
    Io {
        /// The path involved in the operation.
        path: PathBuf,
        /// The underlying I/O error.
        #[source]
        source: std::io::Error,
    },

    /// ZIP parsing or decompression failed.
    #[error("invalid ZIP/JAR archive: {0}")]
    InvalidArchive(String),

    /// A declared or observed limit was exceeded.
    #[error("limit exceeded for {kind}: limit={limit}, actual={actual}")]
    LimitExceeded {
        /// The name of the enforced limit.
        kind: &'static str,
        /// The configured upper bound.
        limit: u64,
        /// The observed value.
        actual: u64,
    },

    /// A path could escape or behave ambiguously during extraction.
    #[error("unsafe archive path {entry:?}: {reason}")]
    UnsafeArchivePath {
        /// The entry path as decoded by the ZIP reader.
        entry: String,
        /// Why the entry is unsafe.
        reason: &'static str,
    },

    /// A public API argument was invalid.
    #[error("invalid argument: {0}")]
    InvalidArgument(String),

    /// Manifest serialization failed.
    #[error("could not serialize scan manifest: {0}")]
    Serialization(String),
}

impl CoreError {
    /// Return the stable C ABI error code for this error.
    #[must_use]
    pub const fn code(&self) -> ErrorCode {
        match self {
            Self::Io { .. } => ErrorCode::Io,
            Self::InvalidArchive(_) => ErrorCode::InvalidArchive,
            Self::LimitExceeded { .. } => ErrorCode::LimitExceeded,
            Self::UnsafeArchivePath { .. } => ErrorCode::UnsafeArchivePath,
            Self::InvalidArgument(_) => ErrorCode::InvalidArgument,
            Self::Serialization(_) => ErrorCode::Serialization,
        }
    }
}
