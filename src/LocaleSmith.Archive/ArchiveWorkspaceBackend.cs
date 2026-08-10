using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Models;

namespace LocaleSmith.Archive;

public sealed class ArchiveWorkspaceBackend : IArchiveWorkspaceBackend
{
    private readonly IArchiveScanner _scanner;
    private readonly ArchiveWorkspaceOptions _options;

    public ArchiveWorkspaceBackend()
        : this(new NativeArchiveScanner(), new ArchiveWorkspaceOptions())
    {
    }

    public ArchiveWorkspaceBackend(
        IArchiveScanner scanner,
        ArchiveWorkspaceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        _scanner = scanner;
        _options = options ?? new ArchiveWorkspaceOptions();
        _options.Validate();
    }

    public async Task<IArchiveWorkspace> BeginAsync(
        Guid jobId,
        PipelineRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("A workspace job id cannot be empty.", nameof(jobId));
        }

        SourceResolution source = ResolveSourcePath(request.SourcePath);
        string requestedOutput = ArchivePathSafety.Canonicalize(request.OutputPath);
        if (string.Equals(source.Path, requestedOutput, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The resolved source and output paths must be different.",
                nameof(request));
        }

        if (source.IsDirectory && ArchivePathSafety.IsSameOrChildPath(source.Path, requestedOutput))
        {
            throw new ArgumentException(
                "A folder output cannot be placed inside the source folder; the source tree is immutable.",
                nameof(request));
        }

        string tempRoot = ArchivePathSafety.Canonicalize(Path.GetTempPath());
        string productRoot = Path.Combine(tempRoot, "LocaleSmith");
        string workspacesRoot = Path.Combine(productRoot, "workspaces");
        string logsRoot = Path.Combine(productRoot, "logs");
        string workspacePath = Path.Combine(workspacesRoot, jobId.ToString("N"));
        bool workspaceCreated = false;
        FileStream? sourceLock = null;
        TransactionJournal? journal = null;
        DirectoryMutationGuard? productRootGuard = null;
        DirectoryMutationGuard? workspacesRootGuard = null;
        DirectoryMutationGuard? logsRootGuard = null;
        DirectoryMutationGuard? workspaceGuard = null;
        try
        {
            string canonicalWorkspace;
            using (ArchiveWorkspaceSetupLock.Acquire(cancellationToken))
            {
                productRootGuard = DirectoryMutationGuard.OpenOrCreateDirectoryForMutation(productRoot);
                workspacesRootGuard = DirectoryMutationGuard.OpenOrCreateChildDirectoryForMutation(
                        productRoot,
                        workspacesRoot)
                    ?? throw new InvalidOperationException("The workspace root must be below the product root.");
                logsRootGuard = DirectoryMutationGuard.OpenOrCreateChildDirectoryForMutation(
                        productRoot,
                        logsRoot)
                    ?? throw new InvalidOperationException("The log root must be below the product root.");
                RejectReparsePoint(productRoot);
                RejectReparsePoint(workspacesRoot);
                RejectReparsePoint(logsRoot);
                ArchivePathSafety.EnsureChildPath(tempRoot, workspacePath);
                if (Directory.Exists(workspacePath) || File.Exists(workspacePath))
                {
                    throw new IOException($"The transaction workspace already exists: '{workspacePath}'.");
                }

                Directory.CreateDirectory(workspacePath);
                workspaceCreated = true;
                workspaceGuard = DirectoryMutationGuard.OpenDirectoryForMutation(workspacePath);
                RejectReparsePoint(workspacePath);
                canonicalWorkspace = ArchivePathSafety.Canonicalize(workspacePath);
                ArchivePathSafety.EnsureChildPath(workspacesRoot, canonicalWorkspace);
                string journalPath = Path.Combine(logsRoot, $"{jobId:N}.jsonl");
                journal = new TransactionJournal(jobId, journalPath);
                logsRootGuard.Dispose();
                logsRootGuard = null;
                workspacesRootGuard.Dispose();
                workspacesRootGuard = null;
                productRootGuard.Dispose();
                productRootGuard = null;
            }

            string transactionSourcePath = source.Path;
            if (source.IsDirectory)
            {
                string snapshotRoot = Path.Combine(canonicalWorkspace, "source-snapshot");
                Directory.CreateDirectory(snapshotRoot);
                using var snapshotGuard = DirectoryMutationGuard.OpenDirectoryForMutation(snapshotRoot);
                RejectReparsePoint(snapshotRoot);
                string sourceName = Path.GetFileName(source.Path);
                string snapshotPath = Path.Combine(snapshotRoot, $"{sourceName}.zip");
                journal.Write("snapshot_folder", "started", $"source={source.Path}; snapshot={snapshotPath}");
                await FolderSnapshotBuilder.CreateSnapshotZipAsync(
                        source.Path,
                        snapshotPath,
                        _options,
                        cancellationToken)
                    .ConfigureAwait(false);
                transactionSourcePath = snapshotPath;
                journal.Write("snapshot_folder", "ok", $"snapshot={snapshotPath}");
            }

            sourceLock = new FileStream(
                transactionSourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.RandomAccess);
            journal.Write(
                "begin",
                "ok",
                $"source={source.Path}; snapshot={transactionSourcePath}; workspace={canonicalWorkspace}");

            IArchiveWorkspace workspace = new ArchiveWorkspace(
                jobId,
                request,
                transactionSourcePath,
                sourceLock,
                canonicalWorkspace,
                ArchivePathSafety.Canonicalize(workspacesRoot),
                _scanner,
                _options,
                journal,
                workspaceGuard,
                source.IsDirectory);
            sourceLock = null;
            journal = null;
            workspaceGuard = null;
            return workspace;
        }
        catch (Exception exception)
        {
            var cleanupErrors = new List<string>();
            try
            {
                journal?.Write("begin", "failed", exception.Message);
            }
            catch (Exception journalException) when (journalException is
                IOException or
                UnauthorizedAccessException or
                ObjectDisposedException)
            {
                // Preserve the primary begin failure while cleanup continues.
            }

            TryCleanup(() => journal?.Dispose(), cleanupErrors, "journal");
            TryCleanup(() => sourceLock?.Dispose(), cleanupErrors, "source lock");
            TryCleanup(() => workspaceGuard?.Dispose(), cleanupErrors, "workspace guard");
            workspaceGuard = null;
            if (workspaceCreated && Directory.Exists(workspacePath))
            {
                TryCleanup(
                    () => DeleteWorkspaceAfterFailedBegin(workspacesRoot, workspacePath),
                    cleanupErrors,
                    "workspace tree");
            }

            if (cleanupErrors.Count > 0)
            {
                exception.Data["LocaleSmith.ArchiveCleanupErrors"] = string.Join(" | ", cleanupErrors);
            }

            throw;
        }
        finally
        {
            workspaceGuard?.Dispose();
            logsRootGuard?.Dispose();
            workspacesRootGuard?.Dispose();
            productRootGuard?.Dispose();
        }
    }

    private static SourceResolution ResolveSourcePath(string requestedPath)
    {
        string fullPath = ArchivePathSafety.Canonicalize(requestedPath);
        if (Directory.Exists(fullPath))
        {
            var directory = new DirectoryInfo(fullPath);
            directory.Refresh();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.LinkTarget is not null)
            {
                throw new InvalidDataException(
                    $"Symbolic links and reparse points are forbidden as folder inputs: '{fullPath}'.");
            }

            return new SourceResolution(fullPath, IsDirectory: true);
        }

        var source = new FileInfo(fullPath);
        if (!source.Exists)
        {
            throw new FileNotFoundException("The source archive does not exist.", fullPath);
        }

        FileSystemInfo? target = source.ResolveLinkTarget(returnFinalTarget: true);
        string resolved = target is null ? source.FullName : target.FullName;
        resolved = ArchivePathSafety.Canonicalize(resolved);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException("The resolved source archive does not exist.", resolved);
        }

        return new SourceResolution(resolved, IsDirectory: false);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Security-sensitive transaction path cannot be a reparse point: '{path}'.");
        }
    }

    private static void DeleteWorkspaceAfterFailedBegin(string workspacesRoot, string workspacePath)
    {
        ArchivePathSafety.EnsureChildPath(workspacesRoot, workspacePath);
        DirectoryMutationGuard.DeleteDirectoryTree(workspacePath);
    }

    private static void TryCleanup(Action action, List<string> errors, string operation)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ObjectDisposedException)
        {
            errors.Add($"{operation}: {exception.Message}");
        }
    }

    private sealed class ArchiveWorkspaceSetupLock : IDisposable
    {
        private const string MutexName = @"Local\DZXH-TX.LocaleSmith.ArchiveWorkspaceSetup.v1";
        private const int AcquireTimeoutMilliseconds = 10_000;
        private readonly Mutex _mutex;
        private bool _ownsMutex;

        private ArchiveWorkspaceSetupLock(Mutex mutex)
        {
            _mutex = mutex;
            _ownsMutex = true;
        }

        public static ArchiveWorkspaceSetupLock Acquire(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mutex = new Mutex(initiallyOwned: false, MutexName);
            try
            {
                try
                {
                    int signaled = WaitHandle.WaitAny(
                        [mutex, cancellationToken.WaitHandle],
                        AcquireTimeoutMilliseconds);
                    if (signaled == WaitHandle.WaitTimeout)
                    {
                        throw new IOException("Timed out while waiting to create a secure archive workspace.");
                    }

                    if (signaled != 0)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }
                catch (AbandonedMutexException)
                {
                    // Ownership is granted when an abandoned mutex is observed. All filesystem
                    // objects are still revalidated under fresh handles below.
                }

                return new ArchiveWorkspaceSetupLock(mutex);
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (!_ownsMutex)
            {
                return;
            }

            _ownsMutex = false;
            try
            {
                _mutex.ReleaseMutex();
            }
            finally
            {
                _mutex.Dispose();
            }
        }
    }

    private sealed record SourceResolution(string Path, bool IsDirectory);
}
