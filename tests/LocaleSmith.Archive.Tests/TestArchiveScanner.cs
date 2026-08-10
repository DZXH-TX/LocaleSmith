using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocaleSmith.Archive;
using LocaleSmith.NativeInterop;

namespace LocaleSmith.Archive.Tests;

internal sealed partial class TestArchiveScanner : IArchiveScanner
{
    public NativeClassStringScan? ClassStringScan { get; init; }

    public ArchiveScanManifest ScanArchive(string archivePath)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        (string? loader, string modId, bool fallback) = ReadMetadata(archive, archivePath);
        var entries = new List<NativeZipEntry>();
        var resources = new List<NativeResourceEntry>();
        var signatureFiles = new List<string>();
        var signatureBlocks = new List<string>();
        bool manifestPresent = false;
        ulong compressed = 0;
        ulong uncompressed = 0;

        for (int index = 0; index < archive.Entries.Count; index++)
        {
            ZipArchiveEntry entry = archive.Entries[index];
            string path = entry.FullName.Replace('\\', '/');
            bool directory = entry.Name.Length == 0;
            ulong compressedLength = checked((ulong)entry.CompressedLength);
            ulong uncompressedLength = checked((ulong)entry.Length);
            compressed = checked(compressed + compressedLength);
            uncompressed = checked(uncompressed + uncompressedLength);
            uint unixMode = unchecked((uint)(entry.ExternalAttributes >> 16)) & 0xFFFF;
            entries.Add(new NativeZipEntry
            {
                Index = checked((ulong)index),
                Path = path,
                EntryType = directory ? "directory" : "file",
                CompressionMethod = entry.CompressedLength == entry.Length ? "Stored" : "Deflated",
                Encrypted = false,
                Crc32 = entry.Crc32,
                CompressedSizeBytes = compressedLength,
                UncompressedSizeBytes = uncompressedLength,
                LastModified = entry.LastWriteTime.ToString("O", CultureInfo.InvariantCulture),
                UnixMode = unixMode,
                Comment = null
            });
            if (TryClassify(path, directory, out string? kind, out string? resourceNamespace, out string? locale))
            {
                resources.Add(new NativeResourceEntry
                {
                    ArchiveIndex = checked((ulong)index),
                    Path = path,
                    Kind = kind,
                    Namespace = resourceNamespace,
                    Locale = locale,
                    Crc32 = entry.Crc32,
                    CompressedSizeBytes = compressedLength,
                    UncompressedSizeBytes = uncompressedLength
                });
            }

            if (string.Equals(path, "META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase))
            {
                manifestPresent = true;
            }
            else if (IsDirectMetaInfFile(path, ".SF"))
            {
                signatureFiles.Add(path);
            }
            else if (IsDirectMetaInfFile(path, ".RSA") ||
                     IsDirectMetaInfFile(path, ".DSA") ||
                     IsDirectMetaInfFile(path, ".EC"))
            {
                signatureBlocks.Add(path);
            }
        }

        string signatureStatus = (signatureFiles.Count, signatureBlocks.Count) switch
        {
            (0, 0) => "none",
            ( > 0, > 0) => "present_unverified",
            _ => "incomplete_unverified"
        };
        var sourceInfo = new FileInfo(archivePath);
        return new ArchiveScanManifest
        {
            SchemaVersion = 1,
            CoreVersion = "test",
            Source = new NativeSourceArchive
            {
                Path = sourceInfo.FullName,
                FileName = sourceInfo.Name,
                SizeBytes = checked((ulong)sourceInfo.Length)
            },
            Archive = new NativeArchiveInventory
            {
                EntryCount = checked((ulong)entries.Count),
                TotalCompressedBytes = compressed,
                TotalUncompressedBytes = uncompressed,
                ArchiveComment = null,
                Entries = entries,
                Signatures = new NativeSignatureEvidence
                {
                    Status = signatureStatus,
                    ManifestPresent = manifestPresent,
                    SignatureFiles = signatureFiles,
                    SignatureBlocks = signatureBlocks,
                    CryptographicallyVerified = false,
                    ModificationBlockedByDefault = signatureStatus != "none",
                    RepackWarning = "test warning"
                }
            },
            ModMetadata = new NativeModMetadata
            {
                DetectionPrecedence = new[] { "fabric.mod.json", "META-INF/mods.toml" },
                PrimaryLoader = loader,
                PrimaryModId = modId,
                ModIds = new[] { modId },
                UsedFilenameFallback = fallback,
                FilenameFallbackNamespace = Sanitize(Path.GetFileNameWithoutExtension(archivePath))
            },
            Resources = resources,
            ClassStringScan = ClassStringScan,
            Warnings = Array.Empty<string>()
        };
    }

    private static (string? Loader, string ModId, bool Fallback) ReadMetadata(
        ZipArchive archive,
        string archivePath)
    {
        ZipArchiveEntry? fabric = archive.GetEntry("fabric.mod.json");
        if (fabric is not null)
        {
            using JsonDocument document = JsonDocument.Parse(ReadText(fabric));
            return ("fabric", document.RootElement.GetProperty("id").GetString()!, false);
        }

        ZipArchiveEntry? forge = archive.GetEntry("META-INF/mods.toml");
        if (forge is not null)
        {
            Match match = ModIdRegex().Match(ReadText(forge));
            if (match.Success)
            {
                return ("forge", match.Groups[1].Value, false);
            }
        }

        return (null, Sanitize(Path.GetFileNameWithoutExtension(archivePath)), true);
    }

    private static bool TryClassify(
        string path,
        bool directory,
        out string kind,
        out string? resourceNamespace,
        out string? locale)
    {
        kind = string.Empty;
        resourceNamespace = null;
        locale = null;
        if (directory)
        {
            return false;
        }

        if (path == "pack.txt")
        {
            kind = "pack_text";
            return true;
        }

        if (path.EndsWith(".mcmeta", StringComparison.Ordinal))
        {
            kind = "mcmeta";
            return true;
        }

        string[] parts = path.Split('/');
        if (parts.Length != 4 || parts[0] != "assets" || parts[2] != "lang")
        {
            return false;
        }

        resourceNamespace = parts[1];
        if (parts[3].EndsWith(".json", StringComparison.Ordinal))
        {
            kind = "language_json";
            locale = parts[3][..^5];
            return true;
        }

        if (parts[3].EndsWith(".lang", StringComparison.Ordinal))
        {
            kind = "language_lang";
            locale = parts[3][..^5];
            return true;
        }

        return false;
    }

    private static bool IsDirectMetaInfFile(string path, string extension)
    {
        if (!path.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fileName = path["META-INF/".Length..];
        return !fileName.Contains('/') && fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Sanitize(string value)
    {
        string sanitized = string.Concat(value.ToLowerInvariant().Select(static character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_'));
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown_mod" : sanitized;
    }

    [GeneratedRegex("modId\\s*=\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ModIdRegex();
}
