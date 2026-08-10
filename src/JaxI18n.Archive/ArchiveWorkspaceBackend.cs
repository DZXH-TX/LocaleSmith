using JaxI18n.Application.Abstractions;
using JaxI18n.Application.Models;

namespace JaxI18n.Archive;

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
        string productRoot = Path.Combine(tempRoot, "JaxI18n");
        string workspacesRoot = Path.Combine(productRoot, "workspaces");
        string logsRoot = Path.Combine(productRoot, "logs");
        string workspacePath = Path.Combine(workspacesRoot, jobId.ToString("N"));
        bool workspaceCreated = false;
        FileStream? sourceLock = null;
        TransactionJournal? journal = null;
        try
        {
            Directory.CreateDirectory(workspacesRoot);
            Directory.CreateDirectory(logsRoot);
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
            RejectReparsePoint(workspacePath);
            string canonicalWorkspace = ArchivePathSafety.Canonicalize(workspacePath);
            ArchivePathSafety.EnsureChildPath(workspacesRoot, canonicalWorkspace);
            string journalPath = Path.Combine(logsRoot, $"{jobId:N}.jsonl");
            journal = new TransactionJournal(jobId, journalPath);

            string transactionSourcePath = source.Path;
            if (source.IsDirectory)
            {
                string snapshotRoot = Path.Combine(canonicalWorkspace, "source-snapshot");
                Directory.CreateDirectory(snapshotRoot);
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
                source.IsDirectory);
            sourceLock = null;
            return workspace;
        }
        catch (Exception exception)
        {
            journal?.Write("begin", "failed", exception.Message);
            sourceLock?.Dispose();
            if (workspaceCreated && Directory.Exists(workspacePath))
            {
                DeleteWorkspaceAfterFailedBegin(workspacesRoot, workspacePath);
            }

            throw;
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
        if ((File.GetAttributes(workspacePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"A failed transaction workspace became a reparse point and was not deleted: '{workspacePath}'.");
        }

        foreach (string child in Directory.EnumerateFileSystemEntries(workspacePath))
        {
            FileAttributes attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
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
                DeleteWorkspaceAfterFailedBegin(workspacePath, child);
            }
            else
            {
                File.Delete(child);
            }
        }

        Directory.Delete(workspacePath, recursive: false);
    }

    private sealed record SourceResolution(string Path, bool IsDirectory);
}
