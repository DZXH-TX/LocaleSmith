using System.Buffers;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocaleSmith.Archive;

/// <summary>
/// Pins checked filesystem objects with handles that do not share delete access. This prevents a
/// checked directory or file from being renamed and replaced while a security-sensitive operation
/// is using its path. Mutable leaf handles also support identity-bound rename and deletion.
/// </summary>
internal sealed class DirectoryMutationGuard : IDisposable
{
    private const uint FileListDirectory = 0x0001;
    private const uint FileReadData = 0x0001;
    private const uint FileWriteData = 0x0002;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileWriteAttributes = 0x0100;
    private const uint DeleteAccess = 0x00010000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const int FileBasicInfo = 0;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private const int FileAttributeTagInfo = 9;

    private readonly SafeFileHandle[] _handles;
    private readonly SafeFileHandle? _mutableLeaf;
    private readonly bool _leafIsDirectory;
    private string? _leafPath;
    private bool _deletePending;
    private bool _disposed;

    private DirectoryMutationGuard(
        SafeFileHandle[] handles,
        SafeFileHandle? mutableLeaf = null,
        bool leafIsDirectory = false,
        string? leafPath = null)
    {
        _handles = handles;
        _mutableLeaf = mutableLeaf;
        _leafIsDirectory = leafIsDirectory;
        _leafPath = leafPath;
    }

    /// <summary>
    /// Opens and validates every directory from the filesystem root through
    /// <paramref name="directory"/>. Use a mutable leaf guard as well when the leaf itself must be
    /// protected against rename or replacement.
    /// </summary>
    public static DirectoryMutationGuard OpenAncestry(string directory)
    {
        EnsureWindows();
        string fullPath = NormalizeDirectory(directory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"The directory ancestry could not be locked because it does not exist: '{fullPath}'.");
        }

        var paths = new List<string>();
        for (DirectoryInfo? current = new(fullPath); current is not null; current = current.Parent)
        {
            paths.Add(current.FullName);
        }

        paths.Reverse();
        return OpenDirectoryPaths(paths);
    }

    /// <summary>
    /// Pins the deepest existing ancestor of a directory that may not have been created yet.
    /// The caller must open the completed ancestry after creating missing components.
    /// </summary>
    public static DirectoryMutationGuard OpenExistingAncestry(string directory)
    {
        EnsureWindows();
        var current = new DirectoryInfo(NormalizeDirectory(directory));
        while (!current.Exists)
        {
            current = current.Parent
                ?? throw new DirectoryNotFoundException(
                    $"The path has no existing directory ancestor: '{directory}'.");
            current.Refresh();
        }

        return OpenAncestry(current.FullName);
    }

    /// <summary>
    /// Pins the deepest existing directory component with mutation-level sharing semantics. This
    /// protects its not-yet-created descendants while the caller creates and validates them.
    /// </summary>
    public static DirectoryMutationGuard OpenDeepestExistingDirectoryForMutation(string directory)
    {
        EnsureWindows();
        var current = new DirectoryInfo(NormalizeDirectory(directory));
        while (!current.Exists)
        {
            current = current.Parent
                ?? throw new DirectoryNotFoundException(
                    $"The path has no existing directory ancestor: '{directory}'.");
            current.Refresh();
        }

        return OpenDirectoryForMutationWithValidatedAncestry(current.FullName);
    }

    /// <summary>
    /// Securely creates a directory path from its deepest existing ancestor, binding every new
    /// component before proceeding deeper.
    /// </summary>
    public static DirectoryMutationGuard OpenOrCreateDirectoryForMutation(string directory)
    {
        EnsureWindows();
        string target = NormalizeDirectory(directory);
        var current = new DirectoryInfo(target);
        while (!current.Exists)
        {
            current = current.Parent
                ?? throw new DirectoryNotFoundException(
                    $"The path has no existing directory ancestor: '{directory}'.");
            current.Refresh();
        }

        DirectoryMutationGuard? existingGuard =
            OpenDirectoryForMutationWithValidatedAncestry(current.FullName);
        try
        {
            DirectoryMutationGuard? targetGuard = OpenChildDirectoryPathForMutation(
                current.FullName,
                target,
                createMissing: true);
            if (targetGuard is null)
            {
                DirectoryMutationGuard result = existingGuard;
                existingGuard = null;
                return result;
            }

            return targetGuard;
        }
        finally
        {
            existingGuard?.Dispose();
        }
    }

    /// <summary>
    /// Opens every existing child component in turn while the caller keeps
    /// <paramref name="guardedRoot"/> pinned. The returned deepest leaf keeps the traversed chain
    /// pinned after intermediate handles are released.
    /// </summary>
    public static DirectoryMutationGuard? OpenExistingChildDirectoryForMutation(
        string guardedRoot,
        string directory) =>
        OpenChildDirectoryPathForMutation(guardedRoot, directory, createMissing: false);

    /// <summary>
    /// Creates missing child components one at a time under a pinned parent and immediately binds
    /// each component to a mutation handle before proceeding deeper.
    /// </summary>
    public static DirectoryMutationGuard? OpenOrCreateChildDirectoryForMutation(
        string guardedRoot,
        string directory) =>
        OpenChildDirectoryPathForMutation(guardedRoot, directory, createMissing: true);

    /// <summary>
    /// Opens an existing directory with the DELETE right while denying delete sharing. The leaf can
    /// then be renamed or deleted by handle without reopening a checked path.
    /// </summary>
    public static DirectoryMutationGuard OpenDirectoryForMutation(string directory)
    {
        EnsureWindows();
        string fullPath = NormalizeDirectory(directory);
        SafeFileHandle handle = OpenHandle(
            fullPath,
            FileListDirectory | FileReadAttributes | FileWriteAttributes | DeleteAccess,
            FileShare.Read | FileShare.Write,
            FileFlagBackupSemantics,
            expectedDirectory: true,
            rejectReparsePoint: true);
        return new DirectoryMutationGuard([handle], handle, leafIsDirectory: true, fullPath);
    }

    /// <summary>
    /// Validates and pins the parent ancestry before opening an otherwise unguarded directory for
    /// mutation. The returned leaf handle retains the ancestry pin.
    /// </summary>
    public static DirectoryMutationGuard OpenDirectoryForMutationWithValidatedAncestry(string directory)
    {
        string fullPath = NormalizeDirectory(directory);
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null)
        {
            return OpenDirectoryForMutation(fullPath);
        }

        using var ancestryGuard = OpenAncestry(parent);
        return OpenDirectoryForMutation(fullPath);
    }

    /// <summary>
    /// Pins a directory for a read-only tree traversal. Write and delete sharing are denied so its
    /// immediate inventory cannot be changed or replaced while it is enumerated.
    /// </summary>
    public static DirectoryMutationGuard OpenDirectoryForTraversal(string directory)
    {
        EnsureWindows();
        string fullPath = NormalizeDirectory(directory);
        SafeFileHandle handle = OpenHandle(
            fullPath,
            FileListDirectory | FileReadAttributes | FileWriteAttributes | DeleteAccess,
            FileShare.Read,
            FileFlagBackupSemantics,
            expectedDirectory: true,
            rejectReparsePoint: true);
        return new DirectoryMutationGuard([handle], handle, leafIsDirectory: true, fullPath);
    }

    /// <summary>
    /// Validates and pins the parent ancestry before opening an otherwise unguarded traversal root.
    /// </summary>
    public static DirectoryMutationGuard OpenDirectoryForTraversalWithValidatedAncestry(string directory)
    {
        string fullPath = NormalizeDirectory(directory);
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null)
        {
            return OpenDirectoryForTraversal(fullPath);
        }

        using var ancestryGuard = OpenAncestry(parent);
        return OpenDirectoryForTraversal(fullPath);
    }

    /// <summary>
    /// Pins a regular file for verification while allowing other read-only consumers to open it.
    /// Writers, deletion, and replacement remain denied until disposal.
    /// </summary>
    public static DirectoryMutationGuard OpenFileForTraversal(string file)
    {
        EnsureWindows();
        string fullPath = Path.GetFullPath(file);
        SafeFileHandle handle = OpenHandle(
            fullPath,
            GenericRead | FileReadData | FileReadAttributes,
            FileShare.Read,
            FileFlagOverlapped | FileFlagSequentialScan,
            expectedDirectory: false,
            rejectReparsePoint: true);
        return new DirectoryMutationGuard([handle], handle, leafIsDirectory: false, fullPath);
    }

    /// <summary>
    /// Opens an existing file for stable hashing, identity-bound rename, and identity-bound delete.
    /// </summary>
    public static DirectoryMutationGuard OpenFileForMutation(string file)
    {
        EnsureWindows();
        string fullPath = Path.GetFullPath(file);
        SafeFileHandle handle = OpenHandle(
            fullPath,
            GenericRead | FileReadData | FileReadAttributes | FileWriteAttributes | DeleteAccess,
            FileShare.Read,
            FileFlagOverlapped | FileFlagSequentialScan,
            expectedDirectory: false,
            rejectReparsePoint: true);
        return new DirectoryMutationGuard([handle], handle, leafIsDirectory: false, fullPath);
    }

    /// <summary>
    /// Atomically creates a new regular file and retains the creating handle for all subsequent
    /// writes, metadata changes, and deletion. The caller must keep the parent directory pinned.
    /// </summary>
    public static DirectoryMutationGuard CreateFileForMutation(string file)
    {
        EnsureWindows();
        string fullPath = Path.GetFullPath(file);
        SafeFileHandle handle = OpenHandle(
            fullPath,
            GenericRead | GenericWrite | FileReadData | FileWriteData |
            FileReadAttributes | FileWriteAttributes | DeleteAccess,
            FileShare.Read,
            FileFlagOverlapped | FileFlagSequentialScan,
            expectedDirectory: false,
            rejectReparsePoint: true,
            creationDisposition: CreateNew);
        return new DirectoryMutationGuard([handle], handle, leafIsDirectory: false, fullPath);
    }

    /// <summary>
    /// Hashes the file represented by the mutable leaf handle. Sharing rules prevent concurrent
    /// writers and replacement for the duration of the operation.
    /// </summary>
    public async Task<byte[]> ComputeFileSha256Async(CancellationToken cancellationToken)
    {
        SafeFileHandle handle = GetMutableLeaf(expectedDirectory: false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long offset = 0;
            while (true)
            {
                int read = await RandomAccess.ReadAsync(
                        handle,
                        buffer.AsMemory(0, buffer.Length),
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                offset = checked(offset + read);
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Renames the mutable leaf object by handle. The target is never overwritten.
    /// </summary>
    public void MoveLeafTo(string targetPath)
    {
        SafeFileHandle handle = GetMutableLeaf(expectedDirectory: null);
        string fullTarget = Path.GetFullPath(targetPath);
        if (File.Exists(fullTarget) || Directory.Exists(fullTarget))
        {
            throw new IOException($"The move target already exists: '{fullTarget}'.");
        }

        byte[] fileName = Encoding.Unicode.GetBytes(fullTarget);
        int rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
        int fileNameLengthOffset = IntPtr.Size == 8 ? 16 : 8;
        int fileNameOffset = IntPtr.Size == 8 ? 20 : 12;
        int structureSize = IntPtr.Size == 8 ? 24 : 16;
        int bufferSize = checked(structureSize + fileName.Length);
        nint buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            // FILE_RENAME_INFO contains a one-character flexible-array placeholder and trailing
            // native alignment. Zero the complete native structure so implementations that read
            // the placeholder/padding cannot append uninitialized characters to the target name.
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            Marshal.WriteIntPtr(buffer, rootDirectoryOffset, nint.Zero);
            Marshal.WriteInt32(buffer, fileNameLengthOffset, fileName.Length);
            Marshal.Copy(fileName, 0, buffer + fileNameOffset, fileName.Length);
            SetInformation(
                handle,
                FileRenameInfo,
                buffer,
                checked((uint)bufferSize),
                "rename",
                _leafPath ?? fullTarget);
            _leafPath = fullTarget;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Marks the mutable leaf object for deletion by handle. It disappears when the guard is
    /// disposed, after all child handles have already been closed.
    /// </summary>
    public void DeleteLeaf()
    {
        SafeFileHandle handle = GetMutableLeaf(expectedDirectory: null);
        ClearReadOnlyAttribute(handle);
        var disposition = new FileDispositionInformation { DeleteFile = 1 };
        SetInformation(handle, FileDispositionInfo, disposition, "delete", _leafPath ?? "<unknown>");
        _deletePending = true;
    }

    /// <summary>
    /// Writes all bytes to a newly-created mutable file without reopening its path.
    /// </summary>
    public async Task WriteFileContentsAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        SafeFileHandle handle = GetMutableLeaf(expectedDirectory: false);
        await RandomAccess.WriteAsync(handle, content, fileOffset: 0, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Copies this guarded source file into another guarded, newly-created file without reopening
    /// either checked path.
    /// </summary>
    public async Task CopyFileToAsync(
        DirectoryMutationGuard destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        SafeFileHandle sourceHandle = GetMutableLeaf(expectedDirectory: false);
        SafeFileHandle destinationHandle = destination.GetMutableLeaf(expectedDirectory: false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long offset = 0;
            while (true)
            {
                int read = await RandomAccess.ReadAsync(
                        sourceHandle,
                        buffer.AsMemory(0, buffer.Length),
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await RandomAccess.WriteAsync(
                        destinationHandle,
                        buffer.AsMemory(0, read),
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                offset = checked(offset + read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Copies a stream into this guarded, newly-created file and fails before writing beyond the
    /// approved maximum length.
    /// </summary>
    public async Task<long> CopyFromStreamAsync(
        Stream source,
        long maximumLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        SafeFileHandle destinationHandle = GetMutableLeaf(expectedDirectory: false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long offset = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return offset;
                }

                long nextOffset = checked(offset + read);
                if (nextOffset > maximumLength)
                {
                    throw new InvalidDataException("The input stream exceeded its approved length.");
                }

                await RandomAccess.WriteAsync(
                        destinationHandle,
                        buffer.AsMemory(0, read),
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                offset = nextOffset;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Copies this guarded file into a stream while hashing the exact bytes read from the bound
    /// handle. The file path is never reopened.
    /// </summary>
    public async Task<(long Length, byte[] Sha256)> CopyFileToStreamAsync(
        Stream destination,
        long maximumLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        SafeFileHandle sourceHandle = GetMutableLeaf(expectedDirectory: false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long offset = 0;
            while (true)
            {
                int read = await RandomAccess.ReadAsync(
                        sourceHandle,
                        buffer.AsMemory(0, buffer.Length),
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return (offset, hash.GetHashAndReset());
                }

                long nextOffset = checked(offset + read);
                if (nextOffset > maximumLength)
                {
                    throw new InvalidDataException("The guarded file exceeded its approved length.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                offset = nextOffset;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Confirms that this live mutable guard represents the expected directory path.
    /// </summary>
    public void EnsurePinsDirectory(string directory)
    {
        GetMutableLeaf(expectedDirectory: true);
        string expected = NormalizeDirectory(directory);
        if (!string.Equals(expected, _leafPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The supplied directory guard does not pin the expected root: '{expected}'.");
        }
    }

    /// <summary>
    /// Applies a last-write timestamp to the guarded leaf without reopening its path.
    /// </summary>
    public void ApplyLeafTimestamp(DateTimeOffset timestamp)
    {
        SafeFileHandle handle = GetMutableLeaf(expectedDirectory: null);
        if (!GetFileInformationByHandleEx(
                handle,
                FileBasicInfo,
                out FileBasicInformation information,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
        {
            throw CreateLastErrorIOException("read metadata", _leafPath ?? "<guarded object>");
        }

        try
        {
            information.LastWriteTime = timestamp.UtcDateTime.ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        SetInformation(
            handle,
            FileBasicInfo,
            information,
            "apply timestamp",
            _leafPath ?? "<guarded object>");
    }

    /// <summary>
    /// Applies the recoverable timestamp and attribute subset to the guarded leaf by handle.
    /// </summary>
    public void ApplyLeafMetadata(DateTimeOffset timestamp, FileAttributes attributes)
    {
        SafeFileHandle handle = GetMutableLeaf(expectedDirectory: null);
        if (!GetFileInformationByHandleEx(
                handle,
                FileBasicInfo,
                out FileBasicInformation information,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
        {
            throw CreateLastErrorIOException("read metadata", _leafPath ?? "<guarded object>");
        }

        try
        {
            information.LastWriteTime = timestamp.UtcDateTime.ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            // Preserve the existing filesystem timestamp when the ZIP value is not representable.
        }

        const FileAttributes restorable = FileAttributes.Archive |
            FileAttributes.Hidden |
            FileAttributes.NotContentIndexed |
            FileAttributes.ReadOnly |
            FileAttributes.System;
        FileAttributes preserved = information.FileAttributes & ~restorable & ~FileAttributes.Normal;
        information.FileAttributes = preserved | (attributes & restorable);
        if (information.FileAttributes == 0)
        {
            information.FileAttributes = FileAttributes.Normal;
        }
        SetInformation(
            handle,
            FileBasicInfo,
            information,
            "apply metadata",
            _leafPath ?? "<guarded object>");
    }

    /// <summary>
    /// Recursively removes a real directory tree without traversing reparse points. Each real
    /// directory and file is opened and validated before mutation; deletions are bound to handles.
    /// </summary>
    public static void DeleteDirectoryTree(string root)
    {
        EnsureWindows();
        string fullRoot = NormalizeDirectory(root);
        string? parent = Path.GetDirectoryName(fullRoot);
        if (parent is null || string.Equals(fullRoot, Path.GetPathRoot(fullRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("A filesystem root cannot be recursively deleted.");
        }

        using var parentGuard = OpenAncestry(parent);
        using var rootGuard = OpenDirectoryForMutation(fullRoot);
        DeleteDirectoryTree(fullRoot, fullRoot, rootGuard);
    }

    /// <summary>
    /// Recursively removes a directory already represented by a mutable guard. The caller must
    /// keep the directory's parent ancestry pinned until this method and guard disposal complete.
    /// </summary>
    public static void DeleteDirectoryTree(
        string approvedRoot,
        string directory,
        DirectoryMutationGuard directoryGuard)
    {
        ArgumentNullException.ThrowIfNull(directoryGuard);
        EnsureWindows();
        string fullRoot = NormalizeDirectory(approvedRoot);
        string fullDirectory = NormalizeDirectory(directory);
        if (!string.Equals(fullRoot, fullDirectory, StringComparison.OrdinalIgnoreCase))
        {
            ArchivePathSafety.EnsureChildPath(fullRoot, fullDirectory);
        }

        directoryGuard.GetMutableLeaf(expectedDirectory: true);
        foreach (string child in Directory.EnumerateFileSystemEntries(fullDirectory))
        {
            ArchivePathSafety.EnsureChildPath(fullRoot, child);
            FileAttributes attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // These operations remove only the link/reparse-point entry. They never recurse
                // into its target. If the entry is concurrently replaced with a normal non-empty
                // directory, the non-recursive delete fails closed.
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(child, recursive: false);
                }
                else
                {
                    File.Delete(child);
                }

                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                using var childGuard = OpenDirectoryForMutation(child);
                DeleteDirectoryTree(fullRoot, child, childGuard);
            }
            else
            {
                using var childGuard = OpenFileForMutation(child);
                childGuard.DeleteLeaf();
            }
        }

        directoryGuard.DeleteLeaf();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (SafeFileHandle handle in _handles.Reverse())
        {
            handle.Dispose();
        }
    }

    private static DirectoryMutationGuard OpenDirectoryPaths(List<string> paths)
    {
        var handles = new List<SafeFileHandle>(paths.Count);
        try
        {
            foreach (string path in paths)
            {
                handles.Add(OpenHandle(
                    path,
                    FileReadAttributes,
                    FileShare.Read | FileShare.Write,
                    FileFlagBackupSemantics,
                    expectedDirectory: true,
                    rejectReparsePoint: true));
            }

            return new DirectoryMutationGuard(handles.ToArray());
        }
        catch
        {
            foreach (SafeFileHandle handle in handles)
            {
                handle.Dispose();
            }

            throw;
        }
    }

    private static DirectoryMutationGuard? OpenChildDirectoryPathForMutation(
        string guardedRoot,
        string directory,
        bool createMissing)
    {
        EnsureWindows();
        string root = NormalizeDirectory(guardedRoot);
        string target = NormalizeDirectory(directory);
        if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ArchivePathSafety.EnsureChildPath(root, target);
        string relative = Path.GetRelativePath(root, target);
        string current = root;
        DirectoryMutationGuard? currentGuard = null;
        try
        {
            foreach (string segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string next = Path.Combine(current, segment);
                if (createMissing)
                {
                    Directory.CreateDirectory(next);
                }

                DirectoryMutationGuard nextGuard = OpenDirectoryForMutation(next);
                currentGuard?.Dispose();
                currentGuard = nextGuard;
                current = next;
            }

            return currentGuard;
        }
        catch
        {
            currentGuard?.Dispose();
            throw;
        }
    }

    private SafeFileHandle GetMutableLeaf(bool? expectedDirectory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_deletePending)
        {
            throw new InvalidOperationException("The guarded filesystem object is already pending deletion.");
        }

        if (_mutableLeaf is null)
        {
            throw new InvalidOperationException("This guard was not opened for mutation.");
        }

        if (expectedDirectory is bool expected && _leafIsDirectory != expected)
        {
            throw new InvalidOperationException(expected
                ? "The guarded filesystem object is not a directory."
                : "The guarded filesystem object is not a file.");
        }

        return _mutableLeaf;
    }

    private static SafeFileHandle OpenHandle(
        string path,
        uint desiredAccess,
        FileShare shareMode,
        uint flags,
        bool expectedDirectory,
        bool rejectReparsePoint,
        uint creationDisposition = OpenExisting)
    {
        SafeFileHandle handle = CreateFile(
            path,
            desiredAccess,
            shareMode,
            nint.Zero,
            creationDisposition,
            flags | FileFlagOpenReparsePoint,
            nint.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"The filesystem object could not be locked against replacement: '{path}'.",
                new Win32Exception(error));
        }

        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out FileAttributeTagInformation attributes,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"The guarded filesystem object attributes could not be verified: '{path}'.",
                new Win32Exception(error));
        }

        bool isDirectory = (attributes.FileAttributes & FileAttributes.Directory) != 0;
        if (isDirectory != expectedDirectory)
        {
            handle.Dispose();
            throw new IOException($"The guarded filesystem object changed type: '{path}'.");
        }

        if (rejectReparsePoint &&
            ((attributes.FileAttributes & FileAttributes.ReparsePoint) != 0 || attributes.ReparseTag != 0))
        {
            handle.Dispose();
            throw new IOException($"The guarded filesystem object is a symbolic link or reparse point: '{path}'.");
        }

        return handle;
    }

    private static void ClearReadOnlyAttribute(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileBasicInfo,
                out FileBasicInformation information,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
        {
            throw CreateLastErrorIOException("read attributes", "<guarded object>");
        }

        if ((information.FileAttributes & FileAttributes.ReadOnly) == 0)
        {
            return;
        }

        information.FileAttributes &= ~FileAttributes.ReadOnly;
        SetInformation(handle, FileBasicInfo, information, "clear read-only attributes", "<guarded object>");
    }

    private static void SetInformation<T>(
        SafeFileHandle handle,
        int informationClass,
        T information,
        string operation,
        string path)
        where T : struct
    {
        int size = Marshal.SizeOf<T>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
            SetInformation(handle, informationClass, buffer, checked((uint)size), operation, path);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetInformation(
        SafeFileHandle handle,
        int informationClass,
        nint buffer,
        uint bufferSize,
        string operation,
        string path)
    {
        if (!SetFileInformationByHandle(handle, informationClass, buffer, bufferSize))
        {
            throw CreateLastErrorIOException(operation, path);
        }
    }

    private static IOException CreateLastErrorIOException(string operation, string path)
    {
        int error = Marshal.GetLastWin32Error();
        return new IOException(
            $"The guarded filesystem object could not complete {operation}: '{path}'.",
            new Win32Exception(error));
    }

    private static string NormalizeDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Secure filesystem mutation requires Windows handle-sharing semantics.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public FileAttributes FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public FileAttributes FileAttributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        public int DeleteFile;
    }

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileBasicInformation fileInformation,
        uint bufferSize);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        nint fileInformation,
        uint bufferSize);
}
