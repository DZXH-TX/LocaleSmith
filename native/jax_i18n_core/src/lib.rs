//! Native, panic-contained ZIP/JAR inspection for the JAX i18n desktop app.
//!
//! The crate deliberately performs no extraction or repacking. It validates
//! archive paths, enforces declared-size limits, inventories central-directory
//! metadata, detects loader metadata, and exposes the result through Rust and C
//! APIs. JAR signing material is evidence only; changing and repacking a signed
//! JAR invalidates that signature and must be an explicit re-signing workflow.

mod classfile;
pub mod error;
pub mod ffi;
mod metadata;
pub mod model;
mod path_safety;
mod scanner;

pub use error::{CoreError, ErrorCode};
pub use model::{
    ClassFileSummary, ClassScanError, ClassStringReference, ClassStringScan,
    MANIFEST_SCHEMA_VERSION, ModLoader, ResourceKind, ScanLimits, ScanManifest,
    SignatureEvidenceStatus,
};
pub use scanner::{scan_archive, scan_archive_json, scan_archive_with_limits};
