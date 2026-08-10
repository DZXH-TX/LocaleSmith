using System.IO.Compression;

namespace JaxI18n.NativeInterop.Tests;

public sealed class NativeCoreClientTests
{
    [Fact]
    public void ScanArchiveReadsFabricMetadataAndResources()
    {
        using var fixture = new ArchiveFixture("fabric-example.jar");
        fixture.AddText("fabric.mod.json", """{"schemaVersion":1,"id":"fabric_example","version":"1.0.0"}""");
        fixture.AddText("assets/fabric_example/lang/en_us.json", """{"item.fabric_example.demo":"Demo"}""");
        fixture.AddText("pack.mcmeta", """{"pack":{"pack_format":34,"description":"Demo pack"}}""");
        fixture.Complete();

        var client = new NativeCoreClient();
        var manifest = client.ScanArchive(fixture.ArchivePath);

        Assert.Equal(1U, manifest.SchemaVersion);
        Assert.Equal("fabric", manifest.ModMetadata.PrimaryLoader);
        Assert.Equal("fabric_example", manifest.ModMetadata.PrimaryModId);
        Assert.False(manifest.ModMetadata.UsedFilenameFallback);
        Assert.Contains(
            manifest.Resources,
            resource => resource.Path == "assets/fabric_example/lang/en_us.json" &&
                resource.Kind == "language_json");
        Assert.Contains(
            manifest.Resources,
            resource => resource.Path == "pack.mcmeta" && resource.Kind == "mcmeta");
        Assert.NotNull(manifest.ClassStringScan);
        Assert.Equal(0UL, manifest.ClassStringScan.DiscoveredClassCount);
        Assert.Contains("never rewritten", manifest.ClassStringScan.MutationPolicy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanArchiveRejectsZipSlipPath()
    {
        using var fixture = new ArchiveFixture("unsafe.jar");
        fixture.AddText("../escape.txt", "blocked");
        fixture.Complete();

        var client = new NativeCoreClient();
        var exception = Assert.Throws<NativeCoreException>(() => client.ScanArchive(fixture.ArchivePath));

        Assert.Equal(NativeCoreErrorCode.UnsafeArchivePath, exception.ErrorCode);
    }

    [Fact]
    public void ScanArchiveUsesSanitizedFileNameFallback()
    {
        using var fixture = new ArchiveFixture("Demo Mod-2.0.jar");
        fixture.AddText("assets/demo/lang/en_us.lang", "demo.key=Demo");
        fixture.Complete();

        var client = new NativeCoreClient();
        var manifest = client.ScanArchive(fixture.ArchivePath);

        Assert.True(manifest.ModMetadata.UsedFilenameFallback);
        Assert.Equal("demo_mod-2.0", manifest.ModMetadata.PrimaryModId);
    }

    private sealed class ArchiveFixture : IDisposable
    {
        private readonly DirectoryInfo _directory;
        private readonly FileStream _stream;
        private readonly ZipArchive _archive;
        private bool _completed;

        public ArchiveFixture(string fileName)
        {
            _directory = Directory.CreateTempSubdirectory("jax-i18n-native-tests-");
            ArchivePath = Path.Combine(_directory.FullName, fileName);
            _stream = new FileStream(ArchivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            _archive = new ZipArchive(_stream, ZipArchiveMode.Create, leaveOpen: true);
        }

        public string ArchivePath { get; }

        public void AddText(string path, string content)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            var entry = _archive.CreateEntry(path, CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
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
            _directory.Delete(recursive: true);
        }
    }
}
