using System.IO.Compression;
using System.Security.Cryptography;
using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Models;
using LocaleSmith.Archive;
using LocaleSmith.Core.Models;
using LocaleSmith.NativeInterop;

namespace LocaleSmith.Archive.Tests;

public sealed class ModArtifactStaticValidationTests
{
    private static readonly IReadOnlySet<TranslationStyle> FormalOnly =
        new HashSet<TranslationStyle> { TranslationStyle.Formal };

    [Fact]
    public async Task PrecompiledJarPassesLayeredStaticValidationWithoutClaimingCompilation()
    {
        using var fixture = new ArchiveFixture("precompiled-static-validation.jar");
        fixture.AddText(
            "fabric.mod.json",
            """
            {
              "schemaVersion": 1,
              "id": "static_demo",
              "entrypoints": { "main": ["example.Test"] },
              "mixins": ["static_demo.mixins.json"],
              "accessWidener": "static_demo.accesswidener",
              "icon": "assets/static_demo/icon.png"
            }
            """);
        fixture.AddText(
            "static_demo.mixins.json",
            """
            {
              "required": true,
              "package": "example",
              "mixins": ["Test"],
              "refmap": "static_demo.refmap.json"
            }
            """);
        fixture.AddText("static_demo.refmap.json", "{}");
        fixture.AddText(
            "static_demo.accesswidener",
            "accessWidener v2 named\naccessible class net/minecraft/client/Minecraft\n");
        fixture.AddText("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\n\r\n");
        fixture.AddText("META-INF/services/example.Service", "example.Test\n");
        fixture.AddBytes("example/Test.class", ClassFileFixtureBuilder.CreateSafeLiteralClass());
        fixture.AddBytes("assets/static_demo/icon.png", [0x89, 0x50, 0x4e, 0x47]);
        fixture.AddText("assets/static_demo/lang/en_us.json", "{\"static_demo.key\":\"Hello\"}");
        fixture.Complete();
        byte[] sourceHash = await ComputeSha256Async(
            fixture.ArchivePath,
            TestContext.Current.CancellationToken);
        PipelineRequest request = CreateRequest(fixture);

        await using IArchiveWorkspace workspace = await new ArchiveWorkspaceBackend().BeginAsync(
            Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);
        await PrepareTranslationsAsync(workspace, request, TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            request.OutputPath,
            TestContext.Current.CancellationToken);

        Assert.True(verification.IsValidArchive);
        Assert.True(verification.MetadataPreserved);
        Assert.Empty(verification.Errors);
        Assert.Equal(ArtifactValidationMode.PrecompiledJarStaticAnalysis, verification.ValidationMode);
        Assert.False(verification.SourceCompilationPerformed);
        Assert.Contains("archive-reopen-and-safe-paths", verification.CompletedChecks!);
        Assert.Contains("json-lang-and-manifest-syntax", verification.CompletedChecks!);
        Assert.Contains("java-class-structure-and-bytecode", verification.CompletedChecks!);
        Assert.Contains("loader-service-and-resource-references", verification.CompletedChecks!);
        await workspace.CommitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            sourceHash,
            await ComputeSha256Async(fixture.ArchivePath, TestContext.Current.CancellationToken));
        using ZipArchive reopened = ZipFile.OpenRead(request.OutputPath);
        Assert.NotNull(reopened.GetEntry("fabric.mod.json"));
        Assert.NotNull(reopened.GetEntry("example/Test.class"));
        Assert.NotNull(reopened.GetEntry("assets/static_demo/lang/zh_cn.json"));
    }

    [Fact]
    public async Task ExistingMissingLoaderReferenceIsWarningAndDoesNotBlockTranslationCopy()
    {
        using var fixture = new ArchiveFixture("broken-loader-reference.jar");
        fixture.AddText(
            "fabric.mod.json",
            "{\"schemaVersion\":1,\"id\":\"broken_demo\",\"entrypoints\":{\"main\":[\"missing.Main\"]}}");
        fixture.AddText("assets/broken_demo/lang/en_us.json", "{\"broken_demo.key\":\"Hello\"}");
        fixture.Complete();
        byte[] sourceHash = await ComputeSha256Async(
            fixture.ArchivePath,
            TestContext.Current.CancellationToken);
        PipelineRequest request = CreateRequest(fixture);

        await using IArchiveWorkspace workspace = await new ArchiveWorkspaceBackend().BeginAsync(
            Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);
        await PrepareTranslationsAsync(workspace, request, TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            request.OutputPath,
            TestContext.Current.CancellationToken);

        Assert.True(verification.IsValidArchive);
        Assert.Empty(verification.Errors);
        Assert.Contains(
            verification.Warnings!,
            static warning => warning.Contains("references missing class 'missing.Main'", StringComparison.Ordinal));
        await workspace.CommitAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(request.OutputPath));
        Assert.Equal(
            sourceHash,
            await ComputeSha256Async(fixture.ArchivePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownAndDuplicateJsonProblemsRemainWarningsWhenUntouched()
    {
        using var fixture = new ArchiveFixture("existing-json-problems.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"warning_demo\"}");
        fixture.AddText("assets/warning_demo/lang/en_us.json", "{\"warning_demo.key\":\"Hello\"}");
        fixture.AddText("config/duplicate.json", "{\"same\":1,\"same\":2}");
        fixture.AddText("config/unknown.json", "{not-json");
        fixture.Complete();
        PipelineRequest request = CreateRequest(fixture);

        await using IArchiveWorkspace workspace = await new ArchiveWorkspaceBackend().BeginAsync(
            Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);
        await PrepareTranslationsAsync(workspace, request, TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            request.OutputPath,
            TestContext.Current.CancellationToken);

        Assert.True(verification.IsValidArchive);
        Assert.Empty(verification.Errors);
        Assert.Contains(
            verification.Warnings!,
            static warning => warning.Contains("duplicate.json", StringComparison.Ordinal) &&
                warning.Contains("repeats property 'same'", StringComparison.Ordinal));
        Assert.Contains(
            verification.Warnings!,
            static warning => warning.Contains("unknown.json", StringComparison.Ordinal));
        await workspace.CommitAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(request.OutputPath));
    }

    [Fact]
    public async Task MultiReleaseClassSatisfiesLoaderReferenceAndBytecodeValidation()
    {
        using var fixture = new ArchiveFixture("multi-release.jar");
        fixture.AddText(
            "fabric.mod.json",
            "{\"schemaVersion\":1,\"id\":\"multi_demo\",\"entrypoints\":{\"main\":[\"example.Test\"]}}");
        fixture.AddText(
            "META-INF/MANIFEST.MF",
            "Manifest-Version: 1.0\r\nMulti-Release: true\r\n\r\n");
        fixture.AddBytes(
            "META-INF/versions/17/example/Test.class",
            ClassFileFixtureBuilder.CreateSafeLiteralClass());
        fixture.AddText("assets/multi_demo/lang/en_us.json", "{\"multi_demo.key\":\"Hello\"}");
        fixture.Complete();
        PipelineRequest request = CreateRequest(fixture);

        await using IArchiveWorkspace workspace = await new ArchiveWorkspaceBackend().BeginAsync(
            Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);
        await PrepareTranslationsAsync(workspace, request, TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            request.OutputPath,
            TestContext.Current.CancellationToken);

        Assert.True(verification.IsValidArchive);
        Assert.Empty(verification.Errors);
        Assert.DoesNotContain(
            verification.Warnings ?? [],
            static warning => warning.Contains("missing class 'example.Test'", StringComparison.Ordinal));
        Assert.Equal(ArtifactValidationMode.PrecompiledJarStaticAnalysis, verification.ValidationMode);
        Assert.False(verification.SourceCompilationPerformed);
        await workspace.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VersionedClassWithoutMultiReleaseManifestCannotSatisfyLoaderReference()
    {
        using var fixture = new ArchiveFixture("missing-multi-release-manifest.jar");
        fixture.AddText(
            "fabric.mod.json",
            "{\"schemaVersion\":1,\"id\":\"not_multi_demo\",\"entrypoints\":{\"main\":[\"example.Test\"]}}");
        fixture.AddBytes(
            "META-INF/versions/17/example/Test.class",
            ClassFileFixtureBuilder.CreateSafeLiteralClass());
        fixture.AddText("assets/not_multi_demo/lang/en_us.json", "{\"not_multi_demo.key\":\"Hello\"}");
        fixture.Complete();
        PipelineRequest request = CreateRequest(fixture);

        await using IArchiveWorkspace workspace = await new ArchiveWorkspaceBackend().BeginAsync(
            Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);
        await PrepareTranslationsAsync(workspace, request, TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            request.OutputPath,
            TestContext.Current.CancellationToken);

        Assert.True(verification.IsValidArchive);
        Assert.Empty(verification.Errors);
        Assert.Contains(
            verification.Warnings!,
            static warning => warning.Contains("does not declare Multi-Release: true", StringComparison.Ordinal));
        Assert.Contains(
            verification.Warnings!,
            static warning => warning.Contains("missing class 'example.Test'", StringComparison.Ordinal));
        await workspace.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InvalidMultiReleaseVersionDirectoryBlocksCommit()
    {
        using var fixture = new ArchiveFixture("invalid-multi-release-version.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"invalid_multi_demo\"}");
        fixture.AddText(
            "META-INF/MANIFEST.MF",
            "Manifest-Version: 1.0\r\nMulti-Release: true\r\n\r\n");
        fixture.AddBytes(
            "META-INF/versions/not-a-version/example/Test.class",
            ClassFileFixtureBuilder.CreateSafeLiteralClass());
        fixture.AddText(
            "assets/invalid_multi_demo/lang/en_us.json",
            "{\"invalid_multi_demo.key\":\"Hello\"}");
        fixture.Complete();
        PipelineRequest request = CreateRequest(fixture);

        await using IArchiveWorkspace workspace = await new ArchiveWorkspaceBackend().BeginAsync(
            Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);
        await PrepareTranslationsAsync(workspace, request, TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            request.OutputPath,
            TestContext.Current.CancellationToken);

        Assert.False(verification.IsValidArchive);
        Assert.Contains(
            verification.Errors,
            static error => error.Contains("invalid Java version directory 'not-a-version'", StringComparison.Ordinal));
        await workspace.RollbackAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(request.OutputPath));
    }

    [Fact]
    public async Task ModifiedNonLanguageJsonAndDuplicateKeysAreBlockingErrors()
    {
        using var fixture = new ArchiveFixture("modified-json-validation.zip");
        fixture.AddText("pack.mcmeta", "{not-json");
        fixture.AddText("config/duplicate.json", "{\"same\":1,\"same\":2}");
        fixture.Complete();
        var modifiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pack.mcmeta",
            "config/duplicate.json"
        };

        ArtifactStaticValidation validation = await ModArtifactStaticValidator.ValidateAsync(
            fixture.ArchivePath,
            modifiedPaths,
            TestContext.Current.CancellationToken);

        Assert.Contains(
            validation.BlockingErrors,
            static error => error.Contains("pack.mcmeta", StringComparison.Ordinal));
        Assert.Contains(
            validation.BlockingErrors,
            static error => error.Contains("duplicate.json", StringComparison.Ordinal) &&
                error.Contains("repeats property 'same'", StringComparison.Ordinal));
        Assert.DoesNotContain(
            validation.Warnings,
            static warning => warning.Contains("duplicate.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SigOnlyArchiveIsDetectedAndPublishedOnlyAsUnsignedCopy()
    {
        using var fixture = new ArchiveFixture("sig-only.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"sig_demo\"}");
        fixture.AddText("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\n\r\n");
        fixture.AddText("META-INF/SIG-CUSTOM", "signature block");
        fixture.AddText("assets/sig_demo/lang/en_us.json", "{\"sig_demo.key\":\"Hello\"}");
        fixture.Complete();
        PipelineRequest request = CreateRequest(fixture, SignedArchiveHandling.CreateUnsignedCopy);

        await using IArchiveWorkspace workspace = await new ArchiveWorkspaceBackend().BeginAsync(
            Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);
        ArchiveInspection inspection = await workspace.InspectAsync(TestContext.Current.CancellationToken);
        Assert.True(inspection.IsSigned);
        await PrepareTranslationsAfterInspectionAsync(workspace, request, TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            request.OutputPath,
            TestContext.Current.CancellationToken);

        Assert.True(verification.IsValidArchive);
        Assert.Empty(verification.Errors);
        await workspace.CommitAsync(TestContext.Current.CancellationToken);
        using ZipArchive output = ZipFile.OpenRead(request.OutputPath);
        Assert.Null(output.GetEntry("META-INF/SIG-CUSTOM"));
    }

    [Fact]
    public async Task ManifestDigestOnlyIsRemovedWithoutClaimingSourceWasSigned()
    {
        using var fixture = new ArchiveFixture("manifest-digest-only.jar");
        fixture.AddText("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"digest_demo\"}");
        fixture.AddText(
            "META-INF/MANIFEST.MF",
            "Manifest-Version: 1.0\r\rName: fabric.mod.json\rSHA-256-Digest: stale\r\r");
        fixture.AddText("assets/digest_demo/lang/en_us.json", "{\"digest_demo.key\":\"Hello\"}");
        fixture.Complete();
        PipelineRequest request = CreateRequest(fixture, SignedArchiveHandling.CreateUnsignedCopy);

        await using IArchiveWorkspace workspace = await new ArchiveWorkspaceBackend().BeginAsync(
            Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);
        ArchiveInspection inspection = await workspace.InspectAsync(TestContext.Current.CancellationToken);
        Assert.False(inspection.IsSigned);
        await PrepareTranslationsAfterInspectionAsync(workspace, request, TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            request.OutputPath,
            TestContext.Current.CancellationToken);

        Assert.True(verification.IsValidArchive);
        Assert.Empty(verification.Errors);
        await workspace.CommitAsync(TestContext.Current.CancellationToken);
        using ZipArchive output = ZipFile.OpenRead(request.OutputPath);
        using var reader = new StreamReader(output.GetEntry("META-INF/MANIFEST.MF")!.Open());
        string manifest = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Digest", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrappedSourceBuildFailsClosedInsteadOfExecutingUntrustedGradle()
    {
        using var fixture = new ArchiveFixture("untrusted-source-build.zip");
        fixture.AddText("README.txt", "root-level file outside the wrapped project");
        fixture.AddText("project/fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"source_demo\"}");
        fixture.AddText("project/build.gradle", "plugins { id 'java' }");
        fixture.AddText("project/src/main/java/example/Main.java", "package example; public final class Main {}");
        fixture.AddText("project/assets/source_demo/lang/en_us.json", "{\"source_demo.key\":\"Hello\"}");
        fixture.Complete();
        PipelineRequest request = CreateRequest(fixture);

        await using IArchiveWorkspace workspace = await new ArchiveWorkspaceBackend().BeginAsync(
            Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);
        await PrepareTranslationsAsync(workspace, request, TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            request.OutputPath,
            TestContext.Current.CancellationToken);

        Assert.False(verification.IsValidArchive);
        Assert.False(verification.SourceCompilationPerformed);
        Assert.Contains(
            verification.Errors,
            static error => error.Contains("will not execute untrusted build scripts", StringComparison.Ordinal));
        await workspace.RollbackAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(request.OutputPath));
    }

    [Fact]
    public async Task NestedBuildEntryWithoutSourcesStillRequiresTrustedCompilation()
    {
        using var fixture = new ArchiveFixture("build-entry-only.zip");
        fixture.AddText("metadata/readme.txt", "not a single-root wrapper");
        fixture.AddText("projects/first/settings.gradle.kts", "rootProject.name = \"first\"");
        fixture.AddText("assets/build_only/lang/en_us.json", "{\"build_only.key\":\"Hello\"}");
        fixture.Complete();
        PipelineRequest request = CreateRequest(fixture);

        await using IArchiveWorkspace workspace = await new ArchiveWorkspaceBackend().BeginAsync(
            Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);
        await PrepareTranslationsAsync(workspace, request, TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            request.OutputPath,
            TestContext.Current.CancellationToken);

        Assert.False(verification.IsValidArchive);
        Assert.False(verification.SourceCompilationPerformed);
        Assert.Contains(
            verification.Errors,
            static error => error.Contains("source or Gradle/build entries were detected", StringComparison.Ordinal));
        await workspace.RollbackAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(request.OutputPath));
    }

    [Fact]
    public async Task StagedDirectoryOverrideTamperIsRejectedBeforeCommit()
    {
        using var fixture = new FolderFixture("tampered-folder-source");
        await fixture.AddTextAsync(
            "fabric.mod.json",
            "{\"schemaVersion\":1,\"id\":\"tamper_demo\"}",
            TestContext.Current.CancellationToken);
        await fixture.AddTextAsync(
            "assets/tamper_demo/lang/en_us.json",
            "{\"tamper_demo.key\":\"Hello\"}",
            TestContext.Current.CancellationToken);
        string sourceHash = await fixture.ComputeSourceHashAsync(TestContext.Current.CancellationToken);
        string outputPath = Path.Combine(fixture.RootPath, "translated-folder");
        var request = new PipelineRequest(
            fixture.SourcePath,
            outputPath,
            targetLanguage: "zh_CN",
            styles: FormalOnly);
        var scanner = new StagedDirectoryTamperingScanner(fixture.RootPath);

        await using IArchiveWorkspace workspace = await new ArchiveWorkspaceBackend(scanner).BeginAsync(
            Guid.NewGuid(),
            request,
            TestContext.Current.CancellationToken);
        await PrepareTranslationsAsync(workspace, request, TestContext.Current.CancellationToken);
        PackageVerification verification = await workspace.StagePackageAsync(
            request.OutputPath,
            TestContext.Current.CancellationToken);

        Assert.True(scanner.Tampered);
        Assert.False(verification.IsValidArchive);
        Assert.Contains(
            verification.Errors,
            static error => error.Contains("staged override content check failed", StringComparison.Ordinal) &&
                error.Contains("does not exactly match", StringComparison.Ordinal));
        await workspace.RollbackAsync(TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(outputPath));
        Assert.Equal(sourceHash, await fixture.ComputeSourceHashAsync(TestContext.Current.CancellationToken));
    }

    private static PipelineRequest CreateRequest(
        ArchiveFixture fixture,
        SignedArchiveHandling signedArchiveHandling = SignedArchiveHandling.Block) =>
        new(
            fixture.ArchivePath,
            Path.Combine(fixture.DirectoryPath, "translated.jar"),
            targetLanguage: "zh_CN",
            styles: FormalOnly,
            signedArchiveHandling: signedArchiveHandling);

    private static async Task PrepareTranslationsAsync(
        IArchiveWorkspace workspace,
        PipelineRequest request,
        CancellationToken cancellationToken)
    {
        await workspace.InspectAsync(cancellationToken);
        await PrepareTranslationsAfterInspectionAsync(workspace, request, cancellationToken);
    }

    private static async Task PrepareTranslationsAfterInspectionAsync(
        IArchiveWorkspace workspace,
        PipelineRequest request,
        CancellationToken cancellationToken)
    {
        await workspace.ExtractAsync(cancellationToken);
        IReadOnlyList<TranslationEntry> entries = await workspace.ReadTranslatableEntriesAsync(cancellationToken);
        TranslatedEntry[] translated = entries.Select(static entry => new TranslatedEntry(
            entry.RelativePath,
            entry.Key,
            SourceHash: string.Empty,
            [new TranslationVariant(TranslationStyle.Formal, $"译:{entry.SourceText}")])).ToArray();
        await workspace.ApplyTranslationsAsync(
            new TranslationBatchResult(request.TargetLanguage, translated),
            cancellationToken);
    }

    private static async Task<byte[]> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }

    private sealed class StagedDirectoryTamperingScanner(string outputRoot) : IArchiveScanner
    {
        private readonly TestArchiveScanner _inner = new();
        private int _scanCount;

        public bool Tampered { get; private set; }

        public ArchiveScanManifest ScanArchive(string archivePath)
        {
            _scanCount++;
            if (_scanCount == 2)
            {
                string stagedDirectory = Directory.EnumerateDirectories(
                        outputRoot,
                        ".translated-folder.*.staged",
                        SearchOption.TopDirectoryOnly)
                    .Single();
                string target = Path.Combine(
                    stagedDirectory,
                    "assets",
                    "tamper_demo",
                    "lang",
                    "zh_cn.json");
                File.WriteAllText(target, "{\"tamper_demo.key\":\"tampered\"}");
                Tampered = true;
            }

            return _inner.ScanArchive(archivePath);
        }
    }
}
