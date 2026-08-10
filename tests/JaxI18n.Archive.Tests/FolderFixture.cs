using System.Security.Cryptography;
using System.Text;

namespace JaxI18n.Archive.Tests;

internal sealed class FolderFixture : IDisposable
{
    public FolderFixture(string folderName)
    {
        RootPath = Directory.CreateTempSubdirectory("jax-i18n-folder-tests-").FullName;
        SourcePath = Path.Combine(RootPath, folderName);
        Directory.CreateDirectory(SourcePath);
    }

    public string RootPath { get; }

    public string SourcePath { get; }

    public async Task AddTextAsync(
        string relativePath,
        string content,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(
            SourcePath,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            throw new InvalidOperationException("A fixture path must have a parent directory.");
        }

        Directory.CreateDirectory(parent);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
    }

    public async Task<string> ComputeSourceHashAsync(CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(SourcePath, "*", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(SourcePath, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData(new byte[] { 0 });
            await using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[4096];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void Dispose()
    {
        if (!Directory.Exists(RootPath))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFileSystemEntries(
                     RootPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(RootPath, recursive: true);
    }
}
