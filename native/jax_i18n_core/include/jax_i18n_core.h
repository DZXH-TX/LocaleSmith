#ifndef JAX_I18N_CORE_H
#define JAX_I18N_CORE_H

#include <stdint.h>

#if defined(_WIN32)
#  define JAX_I18N_API __declspec(dllimport)
#else
#  define JAX_I18N_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Stable return/error codes. */
typedef enum jax_i18n_error_code {
    JAX_I18N_OK = 0,
    JAX_I18N_ERROR_NULL_POINTER = 1,
    JAX_I18N_ERROR_INVALID_UTF8 = 2,
    JAX_I18N_ERROR_IO = 3,
    JAX_I18N_ERROR_INVALID_ARCHIVE = 4,
    JAX_I18N_ERROR_LIMIT_EXCEEDED = 5,
    JAX_I18N_ERROR_UNSAFE_ARCHIVE_PATH = 6,
    JAX_I18N_ERROR_SERIALIZATION = 7,
    JAX_I18N_ERROR_PANIC = 8,
    JAX_I18N_ERROR_INVALID_ARGUMENT = 9
} jax_i18n_error_code;

/* Process-lifetime UTF-8 string. Do not free. */
JAX_I18N_API const char *jax_i18n_core_version(void);

/* Current JSON manifest schema version. */
JAX_I18N_API uint32_t jax_i18n_manifest_schema_version(void);

/*
 * Scan one ZIP/JAR path encoded as NUL-terminated UTF-8.
 *
 * On success, returns JAX_I18N_OK and stores a newly allocated UTF-8 JSON
 * string in *out_json_utf8. Release it once with jax_i18n_string_free.
 * On failure, *out_json_utf8 is null and the thread-local error is updated.
 */
JAX_I18N_API int32_t jax_i18n_scan_archive_json(
    const char *path_utf8,
    char **out_json_utf8);

/* Free a non-static string returned by this library. Null is accepted. */
JAX_I18N_API void jax_i18n_string_free(char *value);

/* Calling-thread error code from the most recent scan call. */
JAX_I18N_API int32_t jax_i18n_last_error_code(void);

/*
 * Copy the calling-thread error message into a newly allocated UTF-8 string.
 * Release the result once with jax_i18n_string_free.
 */
JAX_I18N_API char *jax_i18n_last_error_message(void);

#ifdef __cplusplus
}
#endif

#endif /* JAX_I18N_CORE_H */
