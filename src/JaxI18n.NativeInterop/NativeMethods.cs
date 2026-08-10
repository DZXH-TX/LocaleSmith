using System.Runtime.InteropServices;

namespace JaxI18n.NativeInterop;

internal static partial class NativeMethods
{
    internal const string LibraryName = "jax_i18n_core";

    [LibraryImport(LibraryName, EntryPoint = "jax_i18n_core_version")]
    internal static partial nint CoreVersion();

    [LibraryImport(LibraryName, EntryPoint = "jax_i18n_manifest_schema_version")]
    internal static partial uint ManifestSchemaVersion();

    [LibraryImport(
        LibraryName,
        EntryPoint = "jax_i18n_scan_archive_json",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ScanArchiveJson(string pathUtf8, out nint jsonUtf8);

    [LibraryImport(LibraryName, EntryPoint = "jax_i18n_string_free")]
    internal static partial void StringFree(nint value);

    [LibraryImport(LibraryName, EntryPoint = "jax_i18n_last_error_code")]
    internal static partial int LastErrorCode();

    [LibraryImport(LibraryName, EntryPoint = "jax_i18n_last_error_message")]
    internal static partial nint LastErrorMessage();
}
