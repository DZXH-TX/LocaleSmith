using System.Runtime.InteropServices;

namespace LocaleSmith.NativeInterop;

internal static partial class NativeMethods
{
    internal const string LibraryName = "localesmith_core";

    [LibraryImport(LibraryName, EntryPoint = "localesmith_core_version")]
    internal static partial nint CoreVersion();

    [LibraryImport(LibraryName, EntryPoint = "localesmith_manifest_schema_version")]
    internal static partial uint ManifestSchemaVersion();

    [LibraryImport(
        LibraryName,
        EntryPoint = "localesmith_scan_archive_json",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ScanArchiveJson(string pathUtf8, out nint jsonUtf8);

    [LibraryImport(LibraryName, EntryPoint = "localesmith_string_free")]
    internal static partial void StringFree(nint value);

    [LibraryImport(LibraryName, EntryPoint = "localesmith_last_error_code")]
    internal static partial int LastErrorCode();

    [LibraryImport(LibraryName, EntryPoint = "localesmith_last_error_message")]
    internal static partial nint LastErrorMessage();
}
