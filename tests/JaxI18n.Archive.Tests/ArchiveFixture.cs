using System.IO.Compression;
using System.Text;

namespace JaxI18n.Archive.Tests;

internal sealed class ArchiveFixture : IDisposable
{
    private readonly FileStream _stream;
    private readonly ZipArchive _archive;
    private bool _completed;

    public ArchiveFixture(string fileName)
    {
        DirectoryPath = Directory.CreateTempSubdirectory("jax-i18n-archive-tests-").FullName;
        ArchivePath = Path.Combine(DirectoryPath, fileName);
        _stream = new FileStream(ArchivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        _archive = new ZipArchive(_stream, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
    }

    public string ArchivePath { get; }

    public string DirectoryPath { get; }

    public void AddText(string path, string content)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        ZipArchiveEntry entry = _archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public void AddBytes(string path, ReadOnlySpan<byte> content)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        ZipArchiveEntry entry = _archive.CreateEntry(path, CompressionLevel.Fastest);
        using Stream stream = entry.Open();
        stream.Write(content);
    }

    public void AddSymbolicLink(string path, string target)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        ZipArchiveEntry entry = _archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.ExternalAttributes = unchecked((int)0xA1FF0000);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(target);
    }

    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        _archive.Dispose();
        _stream.Dispose();
        _completed = true;
    }

    public void Dispose()
    {
        Complete();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
