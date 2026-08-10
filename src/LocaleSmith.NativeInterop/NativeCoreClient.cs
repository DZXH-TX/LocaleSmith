using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LocaleSmith.NativeInterop;

[SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "The client is intentionally instance-based so it can be registered and substituted through dependency injection.")]
public sealed class NativeCoreClient
{
    public NativeCoreClient()
    {
        NativeCoreLibraryResolver.EnsureInitialized();
    }

    public string Version
    {
        get
        {
            var value = NativeMethods.CoreVersion();
            return Marshal.PtrToStringUTF8(value)
                ?? throw new NativeCoreException(
                    NativeCoreErrorCode.Panic,
                    "The native core returned a null version string.");
        }
    }

    public uint ManifestSchemaVersion => NativeMethods.ManifestSchemaVersion();

    public string ScanArchiveJson(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (archivePath.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Archive paths cannot contain a null character.", nameof(archivePath));
        }

        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The archive does not exist.", fullPath);
        }

        var status = (NativeCoreErrorCode)NativeMethods.ScanArchiveJson(fullPath, out var jsonPointer);
        if (status != NativeCoreErrorCode.Ok)
        {
            throw CreateException(status);
        }

        if (jsonPointer == nint.Zero)
        {
            throw new NativeCoreException(
                NativeCoreErrorCode.Panic,
                "The native core reported success without returning a manifest.");
        }

        try
        {
            return Marshal.PtrToStringUTF8(jsonPointer)
                ?? throw new NativeCoreException(
                    NativeCoreErrorCode.InvalidUtf8,
                    "The native core returned an invalid UTF-8 manifest.");
        }
        finally
        {
            NativeMethods.StringFree(jsonPointer);
        }
    }

    public ArchiveScanManifest ScanArchive(string archivePath)
    {
        var json = ScanArchiveJson(archivePath);
        ArchiveScanManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(
                    json,
                    NativeManifestJsonContext.Default.ArchiveScanManifest)
                ?? throw new NativeCoreException(
                    NativeCoreErrorCode.Serialization,
                    "The native core returned an empty scan manifest.");
        }
        catch (JsonException exception)
        {
            throw new NativeCoreException(
                NativeCoreErrorCode.Serialization,
                $"The native core returned an invalid scan manifest: {exception.Message}");
        }

        var supportedSchema = ManifestSchemaVersion;
        if (manifest.SchemaVersion != supportedSchema)
        {
            throw new NativeCoreException(
                NativeCoreErrorCode.Serialization,
                $"Unsupported native manifest schema {manifest.SchemaVersion}; expected {supportedSchema}.");
        }

        return manifest;
    }

    private static NativeCoreException CreateException(NativeCoreErrorCode returnedCode)
    {
        var reportedCode = (NativeCoreErrorCode)NativeMethods.LastErrorCode();
        var messagePointer = NativeMethods.LastErrorMessage();
        try
        {
            var message = messagePointer == nint.Zero
                ? "The native core did not provide an error message."
                : Marshal.PtrToStringUTF8(messagePointer)
                    ?? "The native core returned an invalid UTF-8 error message.";

            var effectiveCode = reportedCode == NativeCoreErrorCode.Ok
                ? returnedCode
                : reportedCode;
            return new NativeCoreException(effectiveCode, message);
        }
        finally
        {
            if (messagePointer != nint.Zero)
            {
                NativeMethods.StringFree(messagePointer);
            }
        }
    }
}
