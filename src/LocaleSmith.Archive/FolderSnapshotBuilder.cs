using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocaleSmith.Archive;

internal static class FolderSnapshotBuilder
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorHandleEof = 38;
    private const int StreamNameCapacity = 296;
    private const string DefaultDataStream = "::$DATA";
    private static readonly EnumerationOptions SafeEnumeration = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    public static async Task<FolderSnapshotResult> CreateSnapshotZipAsync(
        string sourceDirectory,
        string snapshotPath,
        ArchiveWorkspaceOptions options,
        CancellationToken cancellationToken,
        DirectoryMutationGuard? sourceRootGuard = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        string sourceRoot = ArchivePathSafety.Canonicalize(sourceDirectory);
        string target = ArchivePathSafety.Canonicalize(snapshotPath);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"The source folder does not exist: '{sourceRoot}'.");
        }

        ArchivePathSafety.RejectReparsePointsInExistingDirectoryAncestry(sourceRoot);
        using DirectoryMutationGuard? ownedRootGuard = sourceRootGuard is null
            ? DirectoryMutationGuard.OpenDirectoryForTraversalWithValidatedAncestry(sourceRoot)
            : null;
        DirectoryMutationGuard activeRootGuard = sourceRootGuard ?? ownedRootGuard!;
        activeRootGuard.EnsurePinsDirectory(sourceRoot);

        if (ArchivePathSafety.IsSameOrChildPath(sourceRoot, target))
        {
            throw new InvalidOperationException("A folder snapshot cannot be written inside its source tree.");
        }

        IReadOnlyList<FolderEntry> baseline = await CaptureInventoryAsync(
                sourceRoot,
                options,
                includeHashes: true,
                activeRootGuard,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            byte[] snapshotSha256;
            await using (var output = new FileStream(
                             target,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             bufferSize: 128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using (var archive = new ZipArchive(
                           output,
                           ZipArchiveMode.Create,
                           leaveOpen: true,
                           entryNameEncoding: Encoding.UTF8))
                {
                    foreach (FolderEntry entry in baseline)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (entry.IsDirectory)
                        {
                            ZipArchiveEntry directoryEntry = archive.CreateEntry(
                                entry.RelativePath + '/',
                                CompressionLevel.NoCompression);
                            directoryEntry.LastWriteTime = ClampZipTimestamp(entry.LastWriteTimeUtc);
                            directoryEntry.ExternalAttributes = checked((int)entry.Attributes);
                            continue;
                        }

                        await AddFileAsync(archive, sourceRoot, entry, cancellationToken).ConfigureAwait(false);
                    }
                }

                output.Position = 0;
                snapshotSha256 = await SHA256.HashDataAsync(output, cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<FolderEntry> finalInventory = await CaptureInventoryAsync(
                    sourceRoot,
                    options,
                    includeHashes: true,
                    activeRootGuard,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureInventoriesMatch(baseline, finalInventory);
            return new FolderSnapshotResult(
                ComputeInventoryDigest(baseline, cancellationToken),
                snapshotSha256);
        }
        catch
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            throw;
        }
    }

    public static async Task<byte[]> ComputeTreeDigestAsync(
        string sourceDirectory,
        ArchiveWorkspaceOptions options,
        CancellationToken cancellationToken,
        DirectoryMutationGuard? sourceRootGuard = null)
    {
        string sourceRoot = ArchivePathSafety.Canonicalize(sourceDirectory);
        using DirectoryMutationGuard? ownedRootGuard = sourceRootGuard is null
            ? DirectoryMutationGuard.OpenDirectoryForTraversalWithValidatedAncestry(sourceRoot)
            : null;
        DirectoryMutationGuard activeRootGuard = sourceRootGuard ?? ownedRootGuard!;
        activeRootGuard.EnsurePinsDirectory(sourceRoot);
        IReadOnlyList<FolderEntry> inventory = await CaptureInventoryAsync(
                sourceRoot,
                options,
                includeHashes: true,
                activeRootGuard,
                cancellationToken)
            .ConfigureAwait(false);
        return ComputeInventoryDigest(inventory, cancellationToken);
    }

    private static byte[] ComputeInventoryDigest(
        IReadOnlyList<FolderEntry> inventory,
        CancellationToken cancellationToken)
    {
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (FolderEntry entry in inventory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendUtf8(digest, entry.RelativePath);
            digest.AppendData(new[] { entry.IsDirectory ? (byte)1 : (byte)0 });
            AppendUtf8(digest, entry.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendUtf8(digest, entry.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendUtf8(digest, ((int)entry.Attributes).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (entry.Sha256 is not null)
            {
                digest.AppendData(entry.Sha256);
            }
        }

        return digest.GetHashAndReset();
    }

    private static async Task<IReadOnlyList<FolderEntry>> CaptureInventoryAsync(
        string sourceRoot,
        ArchiveWorkspaceOptions options,
        bool includeHashes,
        DirectoryMutationGuard sourceRootGuard,
        CancellationToken cancellationToken)
    {
        sourceRootGuard.EnsurePinsDirectory(sourceRoot);
        var root = new DirectoryInfo(sourceRoot);
        RejectUnsafeFileSystemObject(root);
        RejectAlternateDataStreams(sourceRoot);
        var state = new InventoryCaptureState();
        await CaptureDirectoryInventoryAsync(
                sourceRoot,
                sourceRoot,
                parentDepth: 0,
                options,
                includeHashes,
                state,
                cancellationToken)
            .ConfigureAwait(false);
        state.Entries.Sort(
            static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return state.Entries.AsReadOnly();
    }

    private static async Task CaptureDirectoryInventoryAsync(
        string sourceRoot,
        string directory,
        int parentDepth,
        ArchiveWorkspaceOptions options,
        bool includeHashes,
        InventoryCaptureState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileSystemInfo[] children = new DirectoryInfo(directory)
            .EnumerateFileSystemInfos("*", SafeEnumeration)
            .OrderBy(static child => child.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (FileSystemInfo child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            child.Refresh();
            RejectUnsafeFileSystemObject(child);
            string fullPath = ArchivePathSafety.Canonicalize(child.FullName);
            ArchivePathSafety.EnsureChildPath(sourceRoot, fullPath);
            string relativePath = Path.GetRelativePath(sourceRoot, fullPath).Replace('\\', '/');
            relativePath = ArchivePathSafety.ValidateArchiveRelativePath(relativePath).TrimEnd('/');
            if (!state.CollisionKeys.Add(relativePath))
            {
                throw new InvalidDataException(
                    $"Folder entries collide under Windows path normalization: '{relativePath}'.");
            }

            int depth = parentDepth + 1;
            if (depth > options.MaximumDirectoryDepth)
            {
                throw new InvalidDataException(
                    $"Folder exceeds the configured maximum depth at '{relativePath}'.");
            }

            if (state.Entries.Count >= options.MaximumEntryCount)
            {
                throw new InvalidDataException("Folder exceeds the configured entry-count limit.");
            }

            bool isDirectory = (child.Attributes & FileAttributes.Directory) != 0;
            if (isDirectory)
            {
                using var childGuard = DirectoryMutationGuard.OpenDirectoryForTraversal(fullPath);
                var directoryEntry = new DirectoryInfo(fullPath);
                RejectUnsafeFileSystemObject(directoryEntry);
                RejectAlternateDataStreams(fullPath);
                state.Entries.Add(new FolderEntry(
                    relativePath,
                    fullPath,
                    IsDirectory: true,
                    Length: 0,
                    directoryEntry.LastWriteTimeUtc,
                    directoryEntry.CreationTimeUtc,
                    directoryEntry.Attributes,
                    Sha256: null));
                await CaptureDirectoryInventoryAsync(
                        sourceRoot,
                        fullPath,
                        depth,
                        options,
                        includeHashes,
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            using var fileGuard = DirectoryMutationGuard.OpenFileForMutation(fullPath);
            var file = new FileInfo(fullPath);
            RejectUnsafeFileSystemObject(file);
            RejectAlternateDataStreams(fullPath);
            if (file.Length < 0 || file.Length > options.MaximumEntryBytes)
            {
                throw new InvalidDataException(
                    $"Folder file exceeds the configured size limit: '{relativePath}'.");
            }

            state.TotalBytes = checked(state.TotalBytes + file.Length);
            if (state.TotalBytes > options.MaximumTotalBytes)
            {
                throw new InvalidDataException("Folder exceeds the configured total-size limit.");
            }

            byte[]? hash = includeHashes
                ? await fileGuard.ComputeFileSha256Async(cancellationToken).ConfigureAwait(false)
                : null;
            file.Refresh();
            RejectUnsafeFileSystemObject(file);
            state.Entries.Add(new FolderEntry(
                relativePath,
                fullPath,
                IsDirectory: false,
                file.Length,
                file.LastWriteTimeUtc,
                file.CreationTimeUtc,
                file.Attributes,
                hash));
        }
    }

    private static async Task AddFileAsync(
        ZipArchive archive,
        string sourceRoot,
        FolderEntry entry,
        CancellationToken cancellationToken)
    {
        string parent = Path.GetDirectoryName(entry.FullPath)
            ?? throw new InvalidDataException($"Folder entry has no safe parent: '{entry.RelativePath}'.");
        using DirectoryMutationGuard? parentGuard =
            DirectoryMutationGuard.OpenExistingChildDirectoryForMutation(sourceRoot, parent);
        using var sourceGuard = DirectoryMutationGuard.OpenFileForMutation(entry.FullPath);
        EnsureMetadataMatches(entry, new FileInfo(entry.FullPath));
        byte[] beforeHash = await sourceGuard.ComputeFileSha256Async(cancellationToken).ConfigureAwait(false);
        EnsureHashMatches(entry, beforeHash);

        ZipArchiveEntry archiveEntry = archive.CreateEntry(entry.RelativePath, CompressionLevel.Optimal);
        archiveEntry.LastWriteTime = ClampZipTimestamp(entry.LastWriteTimeUtc);
        archiveEntry.ExternalAttributes = checked((int)entry.Attributes);
        await using Stream target = archiveEntry.Open();
        (long copied, byte[] copiedHash) = await sourceGuard
            .CopyFileToStreamAsync(target, entry.Length, cancellationToken)
            .ConfigureAwait(false);

        if (copied != entry.Length)
        {
            throw new InvalidDataException($"Source file size changed while snapshotting: '{entry.RelativePath}'.");
        }

        EnsureHashMatches(entry, copiedHash);
        byte[] afterHash = await sourceGuard.ComputeFileSha256Async(cancellationToken).ConfigureAwait(false);
        EnsureHashMatches(entry, afterHash);
        EnsureMetadataMatches(entry, new FileInfo(entry.FullPath));
    }

    private static void EnsureInventoriesMatch(
        IReadOnlyList<FolderEntry> expected,
        IReadOnlyList<FolderEntry> actual)
    {
        if (expected.Count != actual.Count)
        {
            throw new InvalidDataException("The source folder inventory changed while it was being snapshotted.");
        }

        for (int index = 0; index < expected.Count; index++)
        {
            FolderEntry left = expected[index];
            FolderEntry right = actual[index];
            if (!string.Equals(left.RelativePath, right.RelativePath, StringComparison.Ordinal) ||
                left.IsDirectory != right.IsDirectory ||
                left.Length != right.Length ||
                left.LastWriteTimeUtc != right.LastWriteTimeUtc ||
                left.CreationTimeUtc != right.CreationTimeUtc ||
                left.Attributes != right.Attributes ||
                !HashesEqual(left.Sha256, right.Sha256))
            {
                throw new InvalidDataException(
                    $"The source folder changed while it was being snapshotted near '{left.RelativePath}'.");
            }
        }
    }

    private static void EnsureMetadataMatches(FolderEntry expected, FileInfo current)
    {
        current.Refresh();
        RejectUnsafeFileSystemObject(current);
        if (!current.Exists || current.Length != expected.Length ||
            current.LastWriteTimeUtc != expected.LastWriteTimeUtc ||
            current.CreationTimeUtc != expected.CreationTimeUtc ||
            current.Attributes != expected.Attributes)
        {
            throw new InvalidDataException(
                $"Source file metadata changed while snapshotting: '{expected.RelativePath}'.");
        }
    }

    private static void EnsureHashMatches(FolderEntry expected, byte[] currentHash)
    {
        if (expected.Sha256 is null ||
            !CryptographicOperations.FixedTimeEquals(expected.Sha256, currentHash))
        {
            throw new InvalidDataException(
                $"Source file content changed while snapshotting: '{expected.RelativePath}'.");
        }
    }

    private static bool HashesEqual(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && CryptographicOperations.FixedTimeEquals(left, right);

    private static void RejectUnsafeFileSystemObject(FileSystemInfo item)
    {
        item.Refresh();
        if (!item.Exists)
        {
            throw new IOException($"A folder entry disappeared while being inspected: '{item.FullName}'.");
        }

        if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || item.LinkTarget is not null)
        {
            throw new InvalidDataException(
                $"Symbolic links and reparse points are forbidden in folder inputs: '{item.FullName}'.");
        }
    }

    private static void RejectAlternateDataStreams(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var data = new Win32FindStreamData();
        using SafeFindStreamHandle handle = FindFirstStream(path, 0, out data, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorHandleEof)
            {
                return;
            }

            throw new Win32Exception(error, $"Unable to enumerate NTFS streams for '{path}'.");
        }

        while (true)
        {
            if (!string.Equals(data.StreamName, DefaultDataStream, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Alternate data streams are forbidden in folder inputs: '{path}{data.StreamName}'.");
            }

            if (FindNextStream(handle, out data))
            {
                continue;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorHandleEof)
            {
                return;
            }

            throw new Win32Exception(error, $"Unable to finish enumerating NTFS streams for '{path}'.");
        }
    }

    private static void AppendUtf8(IncrementalHash digest, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        digest.AppendData(bytes);
        digest.AppendData(new byte[] { 0 });
    }

    private static DateTimeOffset ClampZipTimestamp(DateTime timestamp)
    {
        DateTimeOffset value = new(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
        DateTimeOffset minimum = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset maximum = new(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);
        return value < minimum ? minimum : value > maximum ? maximum : value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = StreamNameCapacity)]
        public string StreamName;
    }

    private sealed class SafeFindStreamHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeFindStreamHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindStreamHandle FindFirstStream(
        string fileName,
        int infoLevel,
        out Win32FindStreamData findStreamData,
        uint flags);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(
        SafeFindStreamHandle findStreamHandle,
        out Win32FindStreamData findStreamData);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint findFile);

    private sealed class InventoryCaptureState
    {
        public List<FolderEntry> Entries { get; } = [];

        public HashSet<string> CollisionKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long TotalBytes { get; set; }
    }

    private sealed record FolderEntry(
        string RelativePath,
        string FullPath,
        bool IsDirectory,
        long Length,
        DateTime LastWriteTimeUtc,
        DateTime CreationTimeUtc,
        FileAttributes Attributes,
        byte[]? Sha256);

    internal sealed record FolderSnapshotResult(byte[] TreeSha256, byte[] SnapshotSha256);
}
