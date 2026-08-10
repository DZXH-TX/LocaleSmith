use std::{
    cell::RefCell,
    ffi::{CStr, CString, c_char},
    panic::{AssertUnwindSafe, catch_unwind},
    path::Path,
    ptr,
};

use crate::{
    error::{CoreError, ErrorCode},
    model::MANIFEST_SCHEMA_VERSION,
    scanner::scan_archive_json,
};

const VERSION_C_STRING: &[u8] = concat!(env!("CARGO_PKG_VERSION"), "\0").as_bytes();

#[derive(Debug)]
struct LastError {
    code: ErrorCode,
    message: String,
}

thread_local! {
    static LAST_ERROR: RefCell<LastError> = const {
        RefCell::new(LastError {
            code: ErrorCode::Ok,
            message: String::new(),
        })
    };
}

/// Return the library version as a process-lifetime UTF-8 C string.
///
/// The returned pointer is static and must not be passed to
/// [`localesmith_string_free`].
#[unsafe(no_mangle)]
pub extern "C" fn localesmith_core_version() -> *const c_char {
    VERSION_C_STRING.as_ptr().cast()
}

/// Return the current JSON manifest schema version.
#[unsafe(no_mangle)]
pub extern "C" fn localesmith_manifest_schema_version() -> u32 {
    MANIFEST_SCHEMA_VERSION
}

/// Scan a ZIP/JAR and return a newly allocated compact JSON manifest.
///
/// Returns zero on success. On failure it returns a stable [`ErrorCode`], leaves
/// `*out_json_utf8` null, and updates the calling thread's last error.
///
/// # Safety
///
/// - `path_utf8` must point to a readable NUL-terminated byte string for the
///   duration of this call.
/// - `out_json_utf8` must point to writable storage for one `char *`.
/// - On success the caller owns `*out_json_utf8` and must release it exactly
///   once with [`localesmith_string_free`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn localesmith_scan_archive_json(
    path_utf8: *const c_char,
    out_json_utf8: *mut *mut c_char,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: Requirements are forwarded from this function's documented
        // C ABI contract and checked for null before dereferencing.
        unsafe { scan_archive_json_impl(path_utf8, out_json_utf8) }
    }));
    match result {
        Ok(code) => code,
        Err(payload) => {
            if !out_json_utf8.is_null() {
                // SAFETY: The pointer was null-checked. A caller violating the
                // writable-pointer contract already invokes undefined behavior.
                unsafe { ptr::write(out_json_utf8, ptr::null_mut()) };
            }
            set_last_error(ErrorCode::Panic, panic_message(payload));
            ErrorCode::Panic as i32
        }
    }
}

/// Free a string allocated by this library.
///
/// A null pointer is accepted and ignored.
///
/// # Safety
///
/// `value` must be null or a pointer returned by this library that has not
/// already been freed. Passing any other pointer or freeing twice is undefined
/// behavior.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn localesmith_string_free(value: *mut c_char) {
    if value.is_null() {
        return;
    }
    // SAFETY: Ownership and allocation-origin requirements are the caller's C
    // ABI obligation documented above. `from_raw` is executed exactly once.
    drop(unsafe { CString::from_raw(value) });
}

/// Return the calling thread's most recent error code.
#[unsafe(no_mangle)]
pub extern "C" fn localesmith_last_error_code() -> i32 {
    LAST_ERROR.with(|slot| slot.borrow().code as i32)
}

/// Copy the calling thread's most recent error message into a new C string.
///
/// The returned value is never the version's static pointer. It must be freed
/// with [`localesmith_string_free`]. A null result means allocation/conversion
/// panicked; no panic crosses the ABI.
#[unsafe(no_mangle)]
pub extern "C" fn localesmith_last_error_message() -> *mut c_char {
    catch_unwind(|| {
        let message = LAST_ERROR.with(|slot| slot.borrow().message.clone());
        let without_nuls = message.replace('\0', "\\0");
        CString::new(without_nuls)
            .map(CString::into_raw)
            .unwrap_or(ptr::null_mut())
    })
    .unwrap_or(ptr::null_mut())
}

unsafe fn scan_archive_json_impl(path_utf8: *const c_char, out_json_utf8: *mut *mut c_char) -> i32 {
    if out_json_utf8.is_null() {
        set_last_error(ErrorCode::NullPointer, "out_json_utf8 is null".to_owned());
        return ErrorCode::NullPointer as i32;
    }
    // SAFETY: The pointer was checked for null and the public ABI contract says
    // it points to writable storage for one pointer.
    unsafe { ptr::write(out_json_utf8, ptr::null_mut()) };

    if path_utf8.is_null() {
        set_last_error(ErrorCode::NullPointer, "path_utf8 is null".to_owned());
        return ErrorCode::NullPointer as i32;
    }
    // SAFETY: The public ABI contract requires a NUL-terminated readable C
    // string; the pointer was checked for null immediately above.
    let path_bytes = unsafe { CStr::from_ptr(path_utf8) };
    let path = match path_bytes.to_str() {
        Ok(path) if !path.is_empty() => path,
        Ok(_) => {
            set_last_error(ErrorCode::InvalidArgument, "path is empty".to_owned());
            return ErrorCode::InvalidArgument as i32;
        }
        Err(error) => {
            set_last_error(ErrorCode::InvalidUtf8, error.to_string());
            return ErrorCode::InvalidUtf8 as i32;
        }
    };

    match scan_archive_json(Path::new(path)) {
        Ok(json) => match CString::new(json) {
            Ok(json) => {
                // SAFETY: The output pointer is valid by the public contract and
                // was already null-initialized. Ownership transfers to caller.
                unsafe { ptr::write(out_json_utf8, json.into_raw()) };
                clear_last_error();
                ErrorCode::Ok as i32
            }
            Err(error) => finish_error(CoreError::Serialization(format!(
                "JSON contained an interior NUL: {error}"
            ))),
        },
        Err(error) => finish_error(error),
    }
}

fn finish_error(error: CoreError) -> i32 {
    let code = error.code();
    set_last_error(code, error.to_string());
    code as i32
}

fn clear_last_error() {
    set_last_error(ErrorCode::Ok, String::new());
}

fn set_last_error(code: ErrorCode, message: String) {
    LAST_ERROR.with(|slot| {
        *slot.borrow_mut() = LastError { code, message };
    });
}

fn panic_message(payload: Box<dyn std::any::Any + Send>) -> String {
    if let Some(message) = payload.downcast_ref::<&str>() {
        format!("panic caught at C ABI boundary: {message}")
    } else if let Some(message) = payload.downcast_ref::<String>() {
        format!("panic caught at C ABI boundary: {message}")
    } else {
        "panic caught at C ABI boundary".to_owned()
    }
}

#[cfg(test)]
mod tests {
    use super::{
        localesmith_core_version, localesmith_last_error_code, localesmith_last_error_message,
        localesmith_scan_archive_json, localesmith_string_free,
    };
    use crate::error::ErrorCode;
    use std::ffi::CStr;
    use std::ptr;

    #[test]
    fn version_is_static_utf8() {
        let pointer = localesmith_core_version();
        assert!(!pointer.is_null());
        // SAFETY: The version API promises a process-lifetime NUL-terminated
        // pointer and this test does not mutate or free it.
        let version = unsafe { CStr::from_ptr(pointer) }.to_str().unwrap();
        assert_eq!(version, env!("CARGO_PKG_VERSION"));
    }

    #[test]
    fn null_input_sets_thread_local_error() {
        let mut output = ptr::null_mut();
        // SAFETY: `output` is valid writable pointer storage; a null input path
        // is explicitly handled by the API.
        let code = unsafe { localesmith_scan_archive_json(ptr::null(), &mut output) };
        assert_eq!(code, ErrorCode::NullPointer as i32);
        assert_eq!(localesmith_last_error_code(), ErrorCode::NullPointer as i32);
        assert!(output.is_null());

        let message = localesmith_last_error_message();
        assert!(!message.is_null());
        // SAFETY: `message` came from the library and remains owned until the
        // matching free directly below.
        let text = unsafe { CStr::from_ptr(message) }.to_string_lossy();
        assert!(text.contains("path_utf8"));
        // SAFETY: The pointer was returned by this library and is freed once.
        unsafe { localesmith_string_free(message) };
    }
}
