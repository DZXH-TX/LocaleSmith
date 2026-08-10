using System.IO.Compression;
using System.Text;
using System.Text.Json;
using JaxI18n.Application.Models;
using JaxI18n.Archive;
using JaxI18n.Core.Models;
using JaxI18n.NativeInterop;

namespace JaxI18n.Archive.Tests;

public sealed class ArchiveWorkspaceBackendTests
{
    [Fact]
    public async Task FolderInputBuildsOnlyTheSelectedStyleWithoutChangingSource()
    {
        using var fixture = new FolderFixture("fabric-folder");
        await fixture.AddTextAsync(
            "fabric.mod.json",
            "{\"schemaVersion\":1,\"id\":\"folder_demo\",\"version\":\"1\"}",
            TestContext.Current.CancellationToken);
        await fixture.AddTextAsync(
            "assets/folder_demo/lang/en_us.json",
            "{\"demo.hello\":\"Hello\"}",
            TestContext.Current.CancellationToken);
        await fixture.AddTextAsync(
            "pack.mcmeta",
            "{\"pack\":{\"pack_format\":34,\"description\":\"Folder pack\"}}",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(fixture.SourcePath, "empty"));
        string beforeHash = await fixture.ComputeSourceHashAsync(TestContext.Current.CancellationToken);
        string outputPath = Path.Combine(fixture.RootPath, "translated-folder");
        var request = new PipelineRequest(
            fixture.SourcePath,
            outputPath,
            styles: FormalOnly);
        var backend = new ArchiveWorkspaceBackend(new TestArchiveScanner());

        IReadOnlyList<PackageArtifact> artifacts = await RunWorkspaceAsync(
            backend,
            request,
            static entry => ($"正式:{entry.SourceText}", $"整活:{entry.SourceText}"));

        var artifact = Assert.Single(artifacts);
        Assert.Equal(TranslationStyle.Formal, artifact.Style);
        Assert.Equal(outputPath, artifact.Path);
        Assert.True(Directory.Exists(artifact.Path));
        using JsonDocument formal = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(outputPath, "assets", "folder_demo", "lang", "zh_cn.json"),
            TestContext.Current.CancellationToken));
        Assert.Equal("正式:Hello", formal.RootElement.GetProperty("demo.hello").GetString());
        Assert.True(Directory.Exists(Path.Combine(outputPath, "empty")));
        Assert.Equal(beforeHash, await fixture.ComputeSourceHashAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(fixture.SourcePath, "assets", "folder_demo", "lang", "zh_cn.json")));
    }

    [Fact]
    public async Task FolderInputUsesZipArtifactsWhenOutputRequestsZip()
    {
        using var fixture = new FolderFixture("resource-pack");
        await fixture.AddTextAsync(
            "pack.mcmeta",
            "{\"pack\":{\"pack_format\":34,\"description\":\"Zip me\"}}",
            TestContext.Current.CancellationToken);
        string outputPath = Path.Combine(fixture.RootPath, "translated.zip");
        var request = new PipelineRequest(fixture.SourcePath, outputPath, styles: FormalOnly);

        IReadOnlyList<PackageArtifact> artifacts = await RunWorkspaceAsync(
            new ArchiveWorkspaceBackend(new TestArchiveScanner()),
            request,
            static entry => ($"正式:{entry.SourceText}", string.Empty));

        PackageArtifact artifact = Assert.Single(artifacts);
        Assert.Equal(outputPath, artifact.Path);
        Assert.True(File.Exists(outputPath));
        using ZipArchive output = ZipFile.OpenRead(outputPath);
        using JsonDocument metadata = ReadJson(output, "pack.mcmeta");
        Assert.Equal(
            "正式:Zip me",
            metadata.RootElement.GetProperty("pack").GetProperty("description").GetString());
    }

    [Fact]
    public async Task FolderSnapshotRejectsReparsePointAndLeavesNoWorkspace()
    {
        using var fixture = new FolderFixture("reparse-pack");
        await fixture.AddTextAsync("pack.txt", "Safe", TestContext.Current.CancellationToken);
        string outside = Path.Combine(fixture.RootPath, "outside");
        Directory.CreateDirectory(outside);
        string link = Path.Combine(fixture.SourcePath, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Guid jobId = Guid.NewGuid();
        var request = new PipelineRequest(
            fixture.SourcePath,
            Path.Combine(fixture.RootPath, "translated"),
            styles: FormalOnly);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ArchiveWorkspaceBackend(new TestArchiveScanner()).BeginAsync(
                jobId,
                request,
                TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(GetWorkspacePath(jobId)));
        Assert.False(Directory.Exists(request.OutputPath));
    }

    [Fact]
    public async Task FolderSnapshotRejectsAlternateDataStreamsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new FolderFixture("ads-pack");
        await fixture.AddTextAsync("pack.txt", "Safe", TestContext.Current.CancellationToken);
        string sourceFile = Path.Combine(fixture.SourcePath, "pack.txt");
        try
        {
            await File.WriteAllTextAsync(
                sourceFile + ":hidden",
                "blocked",
                TestContext.Current.CancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return;
        }

        Guid jobId = Guid.NewGuid();
        var request = new PipelineRequest(
            fixture.SourcePath,
            Path.Combine(fixture.RootPath, "translated"),
            styles: FormalOnly);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ArchiveWorkspaceBackend(new TestArchiveScanner()).BeginAsync(
                jobId,
                request,
                TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(GetWorkspacePath(jobId)));
    }

    [Fact]
    public async Task FolderSnapshotLimitFailureCreatesNoOutputOrWorkspace()
    {
        using var fixture = new FolderFixture("limited-pack");
        await fixture.AddTextAsync("pack.txt", "one", TestContext.Current.CancellationToken);
        await fixture.AddTextAsync("pack.mcmeta", "{}", TestContext.Current.CancellationToken);
        Guid jobId = Guid.NewGuid();
        var outputPath = Path.Combine(fixture.RootPath, "translated");
        var request = new PipelineRequest(fixture.SourcePath, outputPath, styles: FormalOnly);
        var backend = new ArchiveWorkspaceBackend(
            new TestArchiveScanner(),
            new ArchiveWorkspaceOptions { MaximumEntryCount = 1 });

        await Assert.ThrowsAsync<InvalidDataException>(
            () => backend.BeginAsync(jobId, request, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(outputPath));
        Assert.False(Directory.Exists(GetWorkspacePath(jobId)));
    }

    [Fact]
    public async Task FolderOutputInsideSourceIsRejectedBeforeSnapshot()
    {
        using var fixture = new FolderFixture("immutable-pack");
        await fixture.AddTextAsync("pack.txt", "unchanged", TestContext.Current.CancellationToken);
        Guid jobId = Guid.NewGuid();
        string outputPath = Path.Combine(fixture.SourcePath, "translated");
        var request = new PipelineRequest(fixture.SourcePath, outputPath, styles: FormalOnly);

        await Assert.ThrowsAsync<ArgumentException>(
            () => new ArchiveWorkspaceBackend(new TestArchiveScanner()).BeginAsync(
                jobId,
                request,
                TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(outputPath));
        Assert.False(Directory.Exists(GetWorkspacePath(jobId)));
        Assert.Equal(
            "unchanged",
            await File.ReadAllTextAsync(
                Path.Combine(fixture.SourcePath, "pack.txt"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FolderSnapshotEnforcesDepthAndTotalSizeLimits()
    {
        using var fixture = new FolderFixture("bounded-pack");
        await fixture.AddTextAsync("one/two/three.txt", "12345", TestContext.Current.CancellationToken);
        string outputPath = Path.Combine(fixture.RootPath, "translated");
        var request = new PipelineRequest(fixture.SourcePath, outputPath, styles: FormalOnly);

        Guid depthJobId = Guid.NewGuid();
        var depthBackend = new ArchiveWorkspaceBackend(
            new TestArchiveScanner(),
            new ArchiveWorkspaceOptions { MaximumDirectoryDepth = 2 });
        await Assert.ThrowsAsync<InvalidDataException>(
            () => depthBackend.BeginAsync(depthJobId, request, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(GetWorkspacePath(depthJobId)));

        Guid sizeJobId = Guid.NewGuid();
        var sizeBackend = new ArchiveWorkspaceBackend(
            new TestArchiveScanner(),
            new ArchiveWorkspaceOptions { MaximumTotalBytes = 4 });
        await Assert.ThrowsAsync<InvalidDataException>(
            () => sizeBackend.BeginAsync(sizeJobId, request, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(GetWorkspacePath(sizeJobId)));
        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public async Task FolderVerificationFailureRollsBackStagingAndCreatesNoOutput()
    {
        using var fixture = new FolderFixture("verification-pack");
        await fixture.AddTextAsync(
            "fabric.mod.json",
            "{\"schemaVersion\":1,\"id\":\"verification_demo\"}",
            TestContext.Current.CancellationToken);
        await fixture.AddTextAsync(
            "assets/verification_demo/lang/en_us.json",
            "{\"demo.key\":\"Demo\"}",
            TestContext.Current.CancellationToken);
        Guid jobId = Guid.NewGuid();
        string outputPath = Path.Combine(fixture.RootPath, "translated");
        var request = new PipelineRequest(fixture.SourcePath, outputPath, styles: FormalOnly);
        var backend = new ArchiveWorkspaceBackend(new FailOnVerificationScanner());

        await using var workspace = await backend.BeginAsync(
            jobId,
            request,
            TestContext.Current.CancellationToken);
        await workspace.InspectAsync(TestContext.Current.CancellationToken);
        await workspace.ExtractAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<TranslationEntry> entries = await workspace.ReadTranslatableEntriesAsync(
            TestContext.Current.CancellationToken);
        await workspace.ApplyTranslationsAsync(
            CreateTranslations(entries, FormalOnly, static entry => ($"正式:{entry.SourceText}", string.Empty)),
            TestContext.Current.CancellationToken);

        PackageVerification verification = await workspace.StagePackageAsync(
            outputPath,
            TestContext.Current.CancellationToken);
        Assert.False(verification.IsValidArchive);
        Assert.False(Directory.Exists(outputPath));
        await workspace.RollbackAsync(TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(outputPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            fixture.RootPath,
            $".{Path.GetFileName(outputPath)}.{jobId:N}.*.staged"));
        Assert.False(Directory.Exists(GetWorkspacePath(jobId)));
    }

    [Fact]
    public async Task DefaultBackendUsesNativeTypedManifest()
    {
        using var fixture = new ArchiveFixture("native-fabric.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"native_demo\",\"version\":\"1\"}");
        fixture.AddText("assets/native_demo/lang/en_us.json", "{\"demo.key\":\"Demo\"}");
        fixture.Complete();
        var backend = new ArchiveWorkspaceBackend();

        await using var workspace = await backend.BeginAsync(
            Guid.NewGuid(),
            CreateRequest(fixture, styles: FormalOnly),
            TestContext.Current.CancellationToken);
        ArchiveInspection inspection = await workspace.InspectAsync(TestContext.Current.CancellationToken);

        Assert.Equal("fabric", inspection.Loader);
        Assert.Equal("native_demo", inspection.ModId);
    }

    [Theory]
    [InlineData("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"fabric_demo\",\"version\":\"1\"}", "fabric", "fabric_demo")]
    [InlineData("META-INF/mods.toml", "modLoader=\"javafml\"\n[[mods]]\nmodId=\"forge_demo\"", "forge", "forge_demo")]
    public async Task InspectMapsFabricAndForgeMetadata(
        string metadataPath,
        string metadata,
        string expectedLoader,
        string expectedModId)
    {
        using var fixture = new ArchiveFixture("metadata.jar");
        fixture.AddText(metadataPath, metadata);
        fixture.AddText($"assets/{expectedModId}/lang/en_us.json", "{\"demo.key\":\"Demo\"}");
        fixture.Complete();
        var request = CreateRequest(fixture, styles: FormalOnly);
        var backend = new ArchiveWorkspaceBackend();

        await using var workspace = await backend.BeginAsync(
            Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);
        ArchiveInspection inspection = await workspace.InspectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedLoader, inspection.Loader);
        Assert.Equal(expectedModId, inspection.ModId);
        Assert.False(inspection.UsedFileNameFallback);
    }

    [Fact]
    public async Task InspectUsesJarFileNameFallbackWhenLoaderMetadataHasNoModId()
    {
        using var fixture = new ArchiveFixture("Demo Mod-2.0.jar");
        fixture.AddText("assets/demo/lang/en_us.lang", "demo.key=Demo");
        fixture.Complete();
        var backend = new ArchiveWorkspaceBackend();

        await using var workspace = await backend.BeginAsync(
            Guid.NewGuid(),
            CreateRequest(fixture, styles: FormalOnly),
            TestContext.Current.CancellationToken);
        ArchiveInspection inspection = await workspace.InspectAsync(TestContext.Current.CancellationToken);

        Assert.True(inspection.UsedFileNameFallback);
        Assert.Equal("demo_mod-2.0", inspection.ModId);
        await workspace.ExtractAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<TranslationEntry> entries = await workspace.ReadTranslatableEntriesAsync(
            TestContext.Current.CancellationToken);
        await workspace.ApplyTranslationsAsync(
            CreateTranslations(entries, FormalOnly, static entry => ("正式:" + entry.SourceText, string.Empty)),
            TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            CreateRequest(fixture, styles: FormalOnly).OutputPath,
            TestContext.Current.CancellationToken);
        Assert.True(verification.MetadataPreserved);
        Assert.Empty(verification.Errors);
    }

    [Fact]
    public async Task ExtractRejectsZipSlipEvenWhenScannerAcceptsIt()
    {
        using var fixture = new ArchiveFixture("unsafe.jar");
        fixture.AddText("../escape.txt", "blocked");
        fixture.Complete();
        Guid jobId = Guid.NewGuid();
        var backend = new ArchiveWorkspaceBackend(new TestArchiveScanner());
        var request = CreateRequest(fixture, styles: FormalOnly);

        await using var workspace = await backend.BeginAsync(
            jobId,
            request,
            TestContext.Current.CancellationToken);
        await workspace.InspectAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => workspace.ExtractAsync(TestContext.Current.CancellationToken));
        await workspace.RollbackAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "escape.txt")));
        Assert.False(Directory.Exists(GetWorkspacePath(jobId)));
    }

    [Fact]
    public async Task BeginHoldsReadOnlySharedSourceLockUntilRollback()
    {
        using var fixture = new ArchiveFixture("locked.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"lock_demo\"}");
        fixture.Complete();
        var backend = new ArchiveWorkspaceBackend(new TestArchiveScanner());

        await using var workspace = await backend.BeginAsync(
            Guid.NewGuid(),
            CreateRequest(fixture, styles: FormalOnly),
            TestContext.Current.CancellationToken);
        Assert.Throws<IOException>(() =>
        {
            using var writer = new FileStream(
                fixture.ArchivePath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
        });

        await workspace.RollbackAsync(TestContext.Current.CancellationToken);
        using var afterRollback = new FileStream(
            fixture.ArchivePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite);
        Assert.True(afterRollback.CanWrite);
    }

    [Fact]
    public async Task ExtractRejectsSymbolicLinkEntries()
    {
        using var fixture = new ArchiveFixture("symlink.jar");
        fixture.AddSymbolicLink("assets/link", "../../outside");
        fixture.Complete();
        var backend = new ArchiveWorkspaceBackend(new TestArchiveScanner());

        await using var workspace = await backend.BeginAsync(
            Guid.NewGuid(),
            CreateRequest(fixture, styles: FormalOnly),
            TestContext.Current.CancellationToken);
        await workspace.InspectAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => workspace.ExtractAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuildsOnlyTheSelectedStyleArtifactAndWritesMcmetaLeaves()
    {
        using var fixture = new ArchiveFixture("fabric.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"demo\",\"version\":\"1\"}");
        fixture.AddText(
            "assets/demo/lang/en_us.json",
            "{\"demo.hello\":\"Hello\",\"demo.world\":\"World\"}");
        fixture.AddText("assets/demo/lang/zh_cn.json", "{\"existing\":\"保留\"}");
        fixture.AddText(
            "pack.mcmeta",
            "{\"pack\":{\"pack_format\":34,\"description\":\"Demo pack\"}}");
        fixture.Complete();
        var request = CreateRequest(fixture);
        var backend = new ArchiveWorkspaceBackend();

        IReadOnlyList<PackageArtifact> artifacts = await RunWorkspaceAsync(
            backend,
            request,
            static entry => ($"正式:{entry.SourceText}", $"整活:{entry.SourceText}"));

        var artifact = Assert.Single(artifacts);
        Assert.Equal(TranslationStyle.Formal, artifact.Style);
        Assert.Equal(request.OutputPath, artifact.Path);

        using ZipArchive formal = ZipFile.OpenRead(artifacts[0].Path);
        using JsonDocument formalLanguage = ReadJson(formal, "assets/demo/lang/zh_cn.json");
        Assert.Equal("正式:Hello", formalLanguage.RootElement.GetProperty("demo.hello").GetString());
        Assert.Equal("正式:World", formalLanguage.RootElement.GetProperty("demo.world").GetString());
        Assert.Equal("保留", formalLanguage.RootElement.GetProperty("existing").GetString());
        using JsonDocument formalMetadata = ReadJson(formal, "pack.mcmeta");
        Assert.Equal(
            "正式:Demo pack",
            formalMetadata.RootElement.GetProperty("pack").GetProperty("description").GetString());

    }

    [Fact]
    public async Task DoesNotTranslateOperationalStringsInUnknownMcmetaSchemas()
    {
        using var fixture = new ArchiveFixture("mcmeta-safety.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"mcmeta_demo\"}");
        fixture.AddText("assets/mcmeta_demo/lang/en_us.json", "{\"demo.key\":\"Demo\"}");
        fixture.AddText(
            "assets/mcmeta_demo/textures/gui.png.mcmeta",
            "{\"animation\":{\"frames\":[\"0\",\"1\"]},\"filter\":{\"block\":[{\"namespace\":\"secret\",\"path\":\"regex\"}]}}");
        fixture.Complete();
        var backend = new ArchiveWorkspaceBackend();

        await using var workspace = await backend.BeginAsync(
            Guid.NewGuid(),
            CreateRequest(fixture, styles: FormalOnly),
            TestContext.Current.CancellationToken);
        await workspace.InspectAsync(TestContext.Current.CancellationToken);
        await workspace.ExtractAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<TranslationEntry> entries = await workspace.ReadTranslatableEntriesAsync(
            TestContext.Current.CancellationToken);

        TranslationEntry language = Assert.Single(entries);
        Assert.Equal("assets/mcmeta_demo/lang/en_us.json", language.RelativePath);
        Assert.DoesNotContain(entries, static entry => entry.RelativePath.EndsWith(".mcmeta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncrementalLangWritebackPreservesUnchangedTargetEntries()
    {
        using var fixture = new ArchiveFixture("forge.jar");
        fixture.AddText("META-INF/mods.toml", "modLoader=\"javafml\"\n[[mods]]\nmodId=\"demo\"");
        fixture.AddText("assets/demo/lang/en_us.lang", "demo.a=A\ndemo.b=B\n");
        fixture.AddText("assets/demo/lang/zh_cn.lang", "# existing\ndemo.a=旧值\ndemo.keep=保留\n");
        fixture.Complete();
        var request = CreateRequest(fixture, styles: FormalOnly);
        var backend = new ArchiveWorkspaceBackend();
        Guid jobId = Guid.NewGuid();

        await using var workspace = await backend.BeginAsync(
            jobId,
            request,
            TestContext.Current.CancellationToken);
        await workspace.InspectAsync(TestContext.Current.CancellationToken);
        await workspace.ExtractAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<TranslationEntry> entries = await workspace.ReadTranslatableEntriesAsync(
            TestContext.Current.CancellationToken);
        TranslationEntry changed = Assert.Single(entries, static entry => entry.Key == "demo.a");
        await workspace.ApplyTranslationsAsync(
            new TranslationBatchResult(
                "zh_CN",
                new[]
                {
                    new TranslatedEntry(
                        changed.RelativePath,
                        changed.Key,
                        "incremental-hash",
                        new[] { new TranslationVariant(TranslationStyle.Formal, "新值") })
                }),
            TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            request.OutputPath,
            TestContext.Current.CancellationToken);
        Assert.Empty(verification.Errors);
        await workspace.CommitAsync(TestContext.Current.CancellationToken);

        using ZipArchive output = ZipFile.OpenRead(request.OutputPath);
        string lang = ReadText(output, "assets/demo/lang/zh_cn.lang");
        Assert.Contains("demo.a=新值", lang, StringComparison.Ordinal);
        Assert.Contains("demo.keep=保留", lang, StringComparison.Ordinal);
        Assert.DoesNotContain("demo.b=", lang, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsignedCopyRemovesSignatureFilesButPreservesManifest()
    {
        using var fixture = new ArchiveFixture("signed.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"signed_demo\"}");
        fixture.AddText("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\n\r\n");
        fixture.AddText("META-INF/DEMO.SF", "signature file");
        fixture.AddText("META-INF/DEMO.RSA", "signature block");
        fixture.AddText("assets/signed_demo/lang/en_us.json", "{\"demo.key\":\"Demo\"}");
        fixture.Complete();
        var request = CreateRequest(
            fixture,
            styles: FormalOnly,
            signedHandling: SignedArchiveHandling.CreateUnsignedCopy);
        var backend = new ArchiveWorkspaceBackend();
        ArchiveInspection? inspection = null;

        await using (var workspace = await backend.BeginAsync(
                         Guid.NewGuid(),
                         request,
                         TestContext.Current.CancellationToken))
        {
            inspection = await workspace.InspectAsync(TestContext.Current.CancellationToken);
            await workspace.ExtractAsync(TestContext.Current.CancellationToken);
            IReadOnlyList<TranslationEntry> entries = await workspace.ReadTranslatableEntriesAsync(
                TestContext.Current.CancellationToken);
            await workspace.ApplyTranslationsAsync(
                CreateTranslations(entries, FormalOnly, static entry => ("正式:" + entry.SourceText, string.Empty)),
                TestContext.Current.CancellationToken);
            PackageVerification verification = await workspace.StagePackageAsync(
                request.OutputPath,
                TestContext.Current.CancellationToken);
            Assert.Empty(verification.Errors);
            await workspace.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.NotNull(inspection);
        Assert.Contains(inspection.Warnings, static warning => warning.Contains("unsigned copy", StringComparison.Ordinal));
        using ZipArchive output = ZipFile.OpenRead(request.OutputPath);
        Assert.NotNull(output.GetEntry("META-INF/MANIFEST.MF"));
        Assert.Null(output.GetEntry("META-INF/DEMO.SF"));
        Assert.Null(output.GetEntry("META-INF/DEMO.RSA"));
    }

    [Fact]
    public async Task CommitCollisionDoesNotOverwriteAndRollbackRemovesTransactionFiles()
    {
        using var fixture = new ArchiveFixture("rollback.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"rollback_demo\"}");
        fixture.AddText("assets/rollback_demo/lang/en_us.json", "{\"demo.key\":\"Demo\"}");
        fixture.Complete();
        Guid jobId = Guid.NewGuid();
        var request = CreateRequest(fixture, styles: FormalOnly);
        var backend = new ArchiveWorkspaceBackend(new TestArchiveScanner());

        await using var workspace = await backend.BeginAsync(
            jobId,
            request,
            TestContext.Current.CancellationToken);
        await workspace.InspectAsync(TestContext.Current.CancellationToken);
        await workspace.ExtractAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<TranslationEntry> entries = await workspace.ReadTranslatableEntriesAsync(
            TestContext.Current.CancellationToken);
        await workspace.ApplyTranslationsAsync(
            CreateTranslations(entries, FormalOnly, static entry => ("正式:" + entry.SourceText, string.Empty)),
            TestContext.Current.CancellationToken);
        await workspace.StagePackageAsync(request.OutputPath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            request.OutputPath,
            "do not overwrite",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(
            () => workspace.CommitAsync(TestContext.Current.CancellationToken));
        await workspace.RollbackAsync(TestContext.Current.CancellationToken);

        Assert.Equal("do not overwrite", await File.ReadAllTextAsync(request.OutputPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.DirectoryPath,
            $".{Path.GetFileName(request.OutputPath)}.{jobId:N}.*.staged"));
        Assert.False(Directory.Exists(GetWorkspacePath(jobId)));
        string journalPath = Path.Combine(Path.GetTempPath(), "JaxI18n", "logs", $"{jobId:N}.jsonl");
        Assert.True(File.Exists(journalPath));
        Assert.Contains("\"operation\":\"rollback\"", await File.ReadAllTextAsync(journalPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CommitRejectsStagedArtifactChangedAfterVerification()
    {
        using var fixture = new ArchiveFixture("tamper.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"tamper_demo\"}");
        fixture.AddText("assets/tamper_demo/lang/en_us.json", "{\"demo.key\":\"Demo\"}");
        fixture.Complete();
        Guid jobId = Guid.NewGuid();
        var request = CreateRequest(fixture, styles: FormalOnly);
        var backend = new ArchiveWorkspaceBackend(new TestArchiveScanner());

        await using var workspace = await backend.BeginAsync(
            jobId,
            request,
            TestContext.Current.CancellationToken);
        await workspace.InspectAsync(TestContext.Current.CancellationToken);
        await workspace.ExtractAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<TranslationEntry> entries = await workspace.ReadTranslatableEntriesAsync(
            TestContext.Current.CancellationToken);
        await workspace.ApplyTranslationsAsync(
            CreateTranslations(entries, FormalOnly, static entry => ("正式:" + entry.SourceText, string.Empty)),
            TestContext.Current.CancellationToken);
        await workspace.StagePackageAsync(request.OutputPath, TestContext.Current.CancellationToken);
        string stagedPath = Path.Combine(
            fixture.DirectoryPath,
            $".{Path.GetFileName(request.OutputPath)}.{jobId:N}.formal.staged");
        await File.AppendAllTextAsync(stagedPath, "tampered", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => workspace.CommitAsync(TestContext.Current.CancellationToken));
        await workspace.RollbackAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(request.OutputPath));
        Assert.False(File.Exists(stagedPath));
    }

    [Fact]
    public async Task NativeScanCandidatesRemainReadOnlyWithoutManagedSafeProof()
    {
        using var fixture = new ArchiveFixture("classes.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"class_demo\"}");
        fixture.AddText("assets/class_demo/lang/en_us.json", "{\"demo.key\":\"Demo\"}");
        fixture.Complete();
        var scanner = new TestArchiveScanner
        {
            ClassStringScan = new NativeClassStringScan
            {
                DiscoveredClassCount = 1,
                SuccessfulClassCount = 1,
                FailedClassCount = 0,
                TotalClassBytes = 128,
                Classes = Array.Empty<NativeClassFileSummary>(),
                References = new[]
                {
                    new NativeClassStringReference
                    {
                        ArchiveIndex = 2,
                        ArchivePath = "demo/Screen.class",
                        Class = "demo/Screen",
                        Method = "render",
                        Descriptor = "()V",
                        BytecodeOffset = 42,
                        Opcode = "ldc",
                        Value = "Open settings",
                        ConstantPoolIndex = 7,
                        Candidate = true,
                        RejectedReason = null
                    }
                },
                Errors = Array.Empty<NativeClassScanError>(),
                MutationPolicy = "read-only"
            }
        };
        var backend = new ArchiveWorkspaceBackend(scanner);

        await using var workspace = await backend.BeginAsync(
            Guid.NewGuid(),
            CreateRequest(fixture, styles: FormalOnly),
            TestContext.Current.CancellationToken);
        await workspace.InspectAsync(TestContext.Current.CancellationToken);
        await workspace.ExtractAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<HardcodedStringCandidate> candidates = await workspace.ScanHardcodedStringsAsync(
            TestContext.Current.CancellationToken);

        HardcodedStringCandidate candidate = Assert.Single(candidates);
        Assert.Equal(2UL, candidate.ArchiveIndex);
        Assert.Equal("demo/Screen.class", candidate.ArchivePath);
        Assert.Equal("demo/Screen", candidate.ClassName);
        Assert.Equal("()V", candidate.MethodDescriptor);
        Assert.Equal(42, candidate.BytecodeOffset);
        Assert.Equal("ldc", candidate.Opcode);
        Assert.Equal(7, candidate.ConstantPoolIndex);
        Assert.StartsWith("class_demo.hardcoded.", candidate.SuggestedKey, StringComparison.Ordinal);
        Assert.False(candidate.IsRecognizedSafePattern);
        await Assert.ThrowsAsync<ArgumentException>(
            () => workspace.ExternalizeAsync(candidates, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => workspace.ExternalizeAsync(
                new[] { candidate with { IsRecognizedSafePattern = true } },
                TestContext.Current.CancellationToken));
    }

    private static readonly IReadOnlySet<TranslationStyle> FormalOnly =
        new HashSet<TranslationStyle> { TranslationStyle.Formal };

    private static PipelineRequest CreateRequest(
        ArchiveFixture fixture,
        IReadOnlySet<TranslationStyle>? styles = null,
        SignedArchiveHandling signedHandling = SignedArchiveHandling.Block) =>
        new(
            fixture.ArchivePath,
            Path.Combine(fixture.DirectoryPath, "translated.jar"),
            styles: styles ?? FormalOnly,
            signedArchiveHandling: signedHandling);

    private static async Task<IReadOnlyList<PackageArtifact>> RunWorkspaceAsync(
        ArchiveWorkspaceBackend backend,
        PipelineRequest request,
        Func<TranslationEntry, (string Formal, string Informal)> translate)
    {
        await using var workspace = await backend.BeginAsync(
            Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);
        await workspace.InspectAsync(TestContext.Current.CancellationToken);
        await workspace.ExtractAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<TranslationEntry> entries = await workspace.ReadTranslatableEntriesAsync(
            TestContext.Current.CancellationToken);
        await workspace.ApplyTranslationsAsync(
            CreateTranslations(entries, request.Styles, translate),
            TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            request.OutputPath,
            TestContext.Current.CancellationToken);
        Assert.True(verification.IsValidArchive);
        Assert.True(verification.MetadataPreserved);
        Assert.Empty(verification.Errors);
        await workspace.CommitAsync(TestContext.Current.CancellationToken);
        return Assert.IsAssignableFrom<IReadOnlyList<PackageArtifact>>(verification.Artifacts);
    }

    private static TranslationBatchResult CreateTranslations(
        IReadOnlyList<TranslationEntry> entries,
        IReadOnlySet<TranslationStyle> styles,
        Func<TranslationEntry, (string Formal, string Informal)> translate)
    {
        TranslatedEntry[] translated = entries.Select(entry =>
        {
            (string formal, string informal) = translate(entry);
            var variants = new List<TranslationVariant>();
            if (styles.Contains(TranslationStyle.Formal))
            {
                variants.Add(new TranslationVariant(TranslationStyle.Formal, formal));
            }

            if (styles.Contains(TranslationStyle.Informal))
            {
                variants.Add(new TranslationVariant(TranslationStyle.Informal, informal));
            }

            return new TranslatedEntry(entry.RelativePath, entry.Key, "test-hash", variants);
        }).ToArray();
        return new TranslationBatchResult("zh_CN", translated);
    }

    private static JsonDocument ReadJson(ZipArchive archive, string path) =>
        JsonDocument.Parse(ReadText(archive, path));

    private static string ReadText(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path)
            ?? throw new InvalidDataException($"Missing test entry '{path}'.");
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string GetWorkspacePath(Guid jobId) =>
        Path.Combine(Path.GetTempPath(), "JaxI18n", "workspaces", jobId.ToString("N"));

    private sealed class FailOnVerificationScanner : IArchiveScanner
    {
        private readonly TestArchiveScanner _inner = new();
        private int _scanCount;

        public ArchiveScanManifest ScanArchive(string archivePath)
        {
            if (Interlocked.Increment(ref _scanCount) == 2)
            {
                throw new InvalidDataException("Injected verification failure.");
            }

            return _inner.ScanArchive(archivePath);
        }
    }
}
