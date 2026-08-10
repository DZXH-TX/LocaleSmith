#ifndef LOCALESMITH_CORE_H
#define LOCALESMITH_CORE_H

#include <stdint.h>

#if defined(_WIN32)
#  define LOCALESMITH_API __declspec(dllimport)
#else
#  define LOCALESMITH_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Stable return/error codes. */
typedef enum localesmith_error_code {
    LOCALESMITH_OK = 0,
    LOCALESMITH_ERROR_NULL_POINTER = 1,
    LOCALESMITH_ERROR_INVALID_UTF8 = 2,
    LOCALESMITH_ERROR_IO = 3,
    LOCALESMITH_ERROR_INVALID_ARCHIVE = 4,
    LOCALESMITH_ERROR_LIMIT_EXCEEDED = 5,
    LOCALESMITH_ERROR_UNSAFE_ARCHIVE_PATH = 6,
    LOCALESMITH_ERROR_SERIALIZATION = 7,
    LOCALESMITH_ERROR_PANIC = 8,
    LOCALESMITH_ERROR_INVALID_ARGUMENT = 9
} localesmith_error_code;

/* Process-lifetime UTF-8 string. Do not free. */
LOCALESMITH_API const char *localesmith_core_version(void);

/* Current JSON manifest schema version. */
LOCALESMITH_API uint32_t localesmith_manifest_schema_version(void);

/*
 * Scan one ZIP/JAR path encoded as NUL-terminated UTF-8.
 *
 * On success, returns LOCALESMITH_OK and stores a newly allocated UTF-8 JSON
 * string in *out_json_utf8. Release it once with localesmith_string_free.
 * On failure, *out_json_utf8 is null and the thread-local error is updated.
 */
LOCALESMITH_API int32_t localesmith_scan_archive_json(
    const char *path_utf8,
    char **out_json_utf8);

/* Free a non-static string returned by this library. Null is accepted. */
LOCALESMITH_API void localesmith_string_free(char *value);

/* Calling-thread error code from the most recent scan call. */
LOCALESMITH_API int32_t localesmith_last_error_code(void);

/*
 * Copy the calling-thread error message into a newly allocated UTF-8 string.
 * Release the result once with localesmith_string_free.
 */
LOCALESMITH_API char *localesmith_last_error_message(void);

#ifdef __cplusplus
}
#endif

#endif /* LOCALESMITH_CORE_H */
