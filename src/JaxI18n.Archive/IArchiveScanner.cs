using JaxI18n.NativeInterop;

namespace JaxI18n.Archive;

public interface IArchiveScanner
{
    ArchiveScanManifest ScanArchive(string archivePath);
}

public sealed class NativeArchiveScanner : IArchiveScanner
{
    private readonly NativeCoreClient _client;

    public NativeArchiveScanner()
        : this(new NativeCoreClient())
    {
    }

    public NativeArchiveScanner(NativeCoreClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public ArchiveScanManifest ScanArchive(string archivePath) => _client.ScanArchive(archivePath);
}
