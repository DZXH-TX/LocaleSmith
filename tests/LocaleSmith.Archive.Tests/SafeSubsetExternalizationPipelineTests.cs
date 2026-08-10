using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LocaleSmith.Application;
using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Models;
using LocaleSmith.Application.Services;
using LocaleSmith.Archive.ClassFile;
using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;
using LocaleSmith.NativeInterop;

namespace LocaleSmith.Archive.Tests;

public sealed class SafeSubsetExternalizationPipelineTests
{
    [Fact]
    public async Task FullPipelinePatchesClassAndBuildsOnlyTheSelectedStyle()
    {
        using var fixture = new ArchiveFixture("safe-subset.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"safe_demo\"}");
        fixture.AddBytes("example/Test.class", ClassFileFixtureBuilder.CreateSafeLiteralClass("Open settings"));
        fixture.AddText("assets/safe_demo/lang/en_us.json", "{\"existing\":\"Existing\"}");
        fixture.AddText("assets/safe_demo/lang/zh_cn.json", "{\"keep\":\"保留\"}");
        fixture.Complete();
        byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(
            fixture.ArchivePath,
            TestContext.Current.CancellationToken));
        var request = CreateExternalizationRequest(
            fixture,
            new HashSet<TranslationStyle> { TranslationStyle.Formal });
        var pipeline = new TranslationPipeline(
            new ArchiveWorkspaceBackend(new TestArchiveScanner()),
            new PrefixTranslationEngine(),
            new MemoryStore());

        PipelineResult result = await pipeline.ExecuteAsync(
            request,
            cancellationToken: TestContext.Current.CancellationToken);

        HardcodedStringCandidate candidate = Assert.Single(
            result.HardcodedCandidates,
            static item => item.IsRecognizedSafePattern);
        Assert.Equal(1, result.Externalization.ExternalizedCount);
        Assert.Contains(
            result.Externalization.Warnings,
            static warning => warning.Contains("verified", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            sourceHash,
            SHA256.HashData(await File.ReadAllBytesAsync(
                fixture.ArchivePath,
                TestContext.Current.CancellationToken)));

        PackageArtifact formalArtifact = Assert.Single(
            result.Artifacts,
            static artifact => artifact.Style == TranslationStyle.Formal);
        byte[] formalClass = ReadBytes(formalArtifact.Path, "example/Test.class");
        MojangComponentLiteralClassFileRewriter.VerifyApplied(
            formalClass,
            new[] { CreateSelection(candidate) });

        using JsonDocument formalTarget = ReadJson(formalArtifact.Path, "assets/safe_demo/lang/zh_cn.json");
        Assert.Equal("保留", formalTarget.RootElement.GetProperty("keep").GetString());
        Assert.Equal("正式:Existing", formalTarget.RootElement.GetProperty("existing").GetString());
        Assert.Equal(
            "正式:Open settings",
            formalTarget.RootElement.GetProperty(candidate.SuggestedKey).GetString());
        using JsonDocument fallback = ReadJson(formalArtifact.Path, "assets/safe_demo/lang/en_us.json");
        Assert.Equal("Existing", fallback.RootElement.GetProperty("existing").GetString());
        Assert.Equal("Open settings", fallback.RootElement.GetProperty(candidate.SuggestedKey).GetString());
    }

    [Fact]
    public async Task FullPipelineCreatesMissingTargetAndFallbackJsonFiles()
    {
        using var fixture = new ArchiveFixture("safe-subset-new-language.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"new_lang_demo\"}");
        fixture.AddBytes("example/Test.class", ClassFileFixtureBuilder.CreateSafeLiteralClass("Brand new text"));
        fixture.Complete();
        var request = CreateExternalizationRequest(
            fixture,
            new HashSet<TranslationStyle> { TranslationStyle.Formal });
        var pipeline = new TranslationPipeline(
            new ArchiveWorkspaceBackend(new TestArchiveScanner()),
            new PrefixTranslationEngine(),
            new MemoryStore());

        PipelineResult result = await pipeline.ExecuteAsync(
            request,
            cancellationToken: TestContext.Current.CancellationToken);

        HardcodedStringCandidate candidate = Assert.Single(
            result.HardcodedCandidates,
            static item => item.IsRecognizedSafePattern);
        using JsonDocument target = ReadJson(request.OutputPath, "assets/new_lang_demo/lang/zh_cn.json");
        using JsonDocument fallback = ReadJson(request.OutputPath, "assets/new_lang_demo/lang/en_us.json");
        Assert.Equal("正式:Brand new text", target.RootElement.GetProperty(candidate.SuggestedKey).GetString());
        Assert.Equal("Brand new text", fallback.RootElement.GetProperty(candidate.SuggestedKey).GetString());
    }

    [Fact]
    public async Task TranslationFailureAfterRewriteRollsBackWithoutProducingArtifacts()
    {
        using var fixture = new ArchiveFixture("safe-subset-rollback.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"rollback_safe_demo\"}");
        fixture.AddBytes("example/Test.class", ClassFileFixtureBuilder.CreateSafeLiteralClass("Rollback text"));
        fixture.Complete();
        byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(
            fixture.ArchivePath,
            TestContext.Current.CancellationToken));
        var request = CreateExternalizationRequest(
            fixture,
            new HashSet<TranslationStyle> { TranslationStyle.Formal });
        Guid jobId = Guid.NewGuid();
        var pipeline = new TranslationPipeline(
            new ArchiveWorkspaceBackend(new TestArchiveScanner()),
            new ThrowingTranslationEngine(),
            new MemoryStore());

        await Assert.ThrowsAsync<PipelineException>(
            () => pipeline.ExecuteAsync(
                request,
                jobId,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(request.OutputPath));
        Assert.False(Directory.Exists(Path.Combine(
            Path.GetTempPath(),
            "LocaleSmith",
            "workspaces",
            jobId.ToString("N"))));
        Assert.Equal(
            sourceHash,
            SHA256.HashData(await File.ReadAllBytesAsync(
            fixture.ArchivePath,
            TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ConflictingExistingFallbackKeyFailsClosedAndRollsBack()
    {
        byte[] classBytes = ClassFileFixtureBuilder.CreateSafeLiteralClass("Collision text");
        ClassFileLiteralCandidate analyzed = Assert.Single(
            MojangComponentLiteralClassFileRewriter.Analyze(classBytes, "collision_demo"),
            static candidate => candidate.IsSafe);
        using var fixture = new ArchiveFixture("safe-subset-fallback-collision.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"collision_demo\"}");
        fixture.AddBytes("example/Test.class", classBytes);
        fixture.AddText(
            "assets/collision_demo/lang/en_us.json",
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [analyzed.SuggestedKey] = "Conflicting existing text",
            }));
        fixture.Complete();
        byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(
            fixture.ArchivePath,
            TestContext.Current.CancellationToken));
        var request = CreateExternalizationRequest(
            fixture,
            new HashSet<TranslationStyle> { TranslationStyle.Formal });
        Guid jobId = Guid.NewGuid();
        var pipeline = new TranslationPipeline(
            new ArchiveWorkspaceBackend(new TestArchiveScanner()),
            new PrefixTranslationEngine(),
            new MemoryStore());

        PipelineException exception = await Assert.ThrowsAsync<PipelineException>(
            () => pipeline.ExecuteAsync(
                request,
                jobId,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(PipelineStage.Analyzing, exception.FailedStage);
        Assert.Contains(
            "conflicts",
            Assert.IsType<InvalidDataException>(exception.InnerException).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(request.OutputPath));
        Assert.False(Directory.Exists(Path.Combine(
            Path.GetTempPath(),
            "LocaleSmith",
            "workspaces",
            jobId.ToString("N"))));
        Assert.Equal(
            sourceHash,
            SHA256.HashData(await File.ReadAllBytesAsync(
                fixture.ArchivePath,
                TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task StagingRescanRejectsClassWhoseVerifiedRewriteWasReverted()
    {
        byte[] originalClass = ClassFileFixtureBuilder.CreateSafeLiteralClass("Tamper text");
        using var fixture = new ArchiveFixture("safe-subset-stage-tamper.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"tamper_safe_demo\"}");
        fixture.AddBytes("example/Test.class", originalClass);
        fixture.Complete();
        var request = CreateExternalizationRequest(
            fixture,
            new HashSet<TranslationStyle> { TranslationStyle.Formal });
        Guid jobId = Guid.NewGuid();
        var pipeline = new TranslationPipeline(
            new ArchiveWorkspaceBackend(new RevertClassAfterStagedScanScanner(originalClass)),
            new PrefixTranslationEngine(),
            new MemoryStore());

        PipelineException exception = await Assert.ThrowsAsync<PipelineException>(
            () => pipeline.ExecuteAsync(
                request,
                jobId,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(PipelineStage.Verifying, exception.FailedStage);
        Assert.Contains("safe-subset", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(request.OutputPath));
        Assert.False(Directory.Exists(Path.Combine(
            Path.GetTempPath(),
            "LocaleSmith",
            "workspaces",
            jobId.ToString("N"))));
    }

    private static PipelineRequest CreateExternalizationRequest(
        ArchiveFixture fixture,
        IReadOnlySet<TranslationStyle>? styles = null) =>
        new(
            fixture.ArchivePath,
            Path.Combine(fixture.DirectoryPath, "translated.jar"),
            styles: styles,
            hardcodedStringMode: HardcodedStringMode.ExternalizeRecognizedSafePatterns);

    private static ClassFileRewriteSelection CreateSelection(HardcodedStringCandidate candidate) =>
        new(
            candidate.ClassName,
            candidate.MethodName,
            candidate.MethodDescriptor,
            candidate.BytecodeOffset,
            candidate.Value,
            candidate.SuggestedKey);

    private static JsonDocument ReadJson(string archivePath, string entryPath) =>
        JsonDocument.Parse(ReadBytes(archivePath, entryPath));

    private static byte[] ReadBytes(string archivePath, string entryPath)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry entry = archive.GetEntry(entryPath)
            ?? throw new InvalidDataException($"Missing test entry '{entryPath}'.");
        using Stream stream = entry.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private sealed class PrefixTranslationEngine : ITranslationEngine
    {
        public string TranslationContractVersion => "safe-subset-test/v1";

        public Task<TranslationBatchResult> TranslateAsync(
            TranslationBatchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TranslatedEntry[] translated = request.Entries.Select(entry => new TranslatedEntry(
                entry.RelativePath,
                entry.Key,
                IncrementalTranslationPlanner.ComputeHash(entry),
                request.Styles.Select(style => new TranslationVariant(
                    style,
                    $"{(style == TranslationStyle.Formal ? "正式" : "整活")}:{entry.SourceText}"))
                    .ToArray())).ToArray();
            return Task.FromResult(new TranslationBatchResult(request.TargetLanguage, translated));
        }
    }

    private sealed class ThrowingTranslationEngine : ITranslationEngine
    {
        public string TranslationContractVersion => "safe-subset-test/failure";

        public Task<TranslationBatchResult> TranslateAsync(
            TranslationBatchRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated translation failure after verified rewrite");
    }

    private sealed class MemoryStore : ITranslationMemoryStore
    {
        public Task<TranslationMemorySnapshot> LoadAsync(
            TranslationMemoryKey key,
            CancellationToken cancellationToken) =>
            Task.FromResult(TranslationMemorySnapshot.Empty(key));

        public Task SaveAsync(
            TranslationMemorySnapshot snapshot,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RevertClassAfterStagedScanScanner(byte[] originalClass) : IArchiveScanner
    {
        private readonly TestArchiveScanner _inner = new();
        private int _scanCount;

        public ArchiveScanManifest ScanArchive(string archivePath)
        {
            ArchiveScanManifest manifest = _inner.ScanArchive(archivePath);
            if (Interlocked.Increment(ref _scanCount) != 2)
            {
                return manifest;
            }

            using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
            ZipArchiveEntry entry = archive.GetEntry("example/Test.class")
                ?? throw new InvalidDataException("Missing class selected for staged tamper test.");
            entry.Delete();
            ZipArchiveEntry replacement = archive.CreateEntry("example/Test.class", CompressionLevel.Fastest);
            using Stream stream = replacement.Open();
            stream.Write(originalClass);
            return manifest;
        }
    }
}
