using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Models;
using LocaleSmith.Application.Services;
using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;

namespace LocaleSmith.Application.Tests;

public sealed class TranslationPipelineTests
{
    private static readonly IReadOnlyList<string> VerificationErrors =
        new[] { "central directory invalid" };

    [Fact]
    public void PipelineRequestRejectsMultipleOrUnknownStyles()
    {
        var prefix = Path.Combine(Path.GetTempPath(), "localesmith-tests");

        Assert.Throws<ArgumentException>(() => new PipelineRequest(
            Path.Combine(prefix, "input.jar"),
            Path.Combine(prefix, "output.jar"),
            styles: new HashSet<TranslationStyle>
            {
                TranslationStyle.Formal,
                TranslationStyle.Informal
            }));
        Assert.Throws<ArgumentException>(() => new PipelineRequest(
            Path.Combine(prefix, "input.jar"),
            Path.Combine(prefix, "output.jar"),
            styles: new HashSet<TranslationStyle> { (TranslationStyle)99 }));
    }

    [Fact]
    public async Task ExecuteAsyncCommitsVerifiedPackage()
    {
        var workspace = new StubWorkspace();
        var memory = new StubMemoryStore
        {
            IsWorkspaceCommitted = () => workspace.Committed
        };
        var engine = new StubTranslationEngine();
        var pipeline = new TranslationPipeline(
            new StubWorkspaceBackend(workspace),
            engine,
            memory);
        var progressUpdates = new List<PipelineProgress>();

        var result = await pipeline.ExecuteAsync(
            CreateRequest(),
            new InlineProgress(progressUpdates.Add),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(workspace.Committed);
        Assert.False(workspace.RolledBack);
        Assert.Equal(1, result.TranslatedEntryCount);
        Assert.Equal(0, result.ReusedEntryCount);
        Assert.NotNull(workspace.AppliedTranslations);
        Assert.NotNull(memory.Saved);
        Assert.True(memory.WorkspaceWasCommittedWhenSaved);
        Assert.Empty(result.Warnings);
        Assert.Equal(1, engine.CallCount);
        Assert.NotNull(engine.LastRequest);
        Assert.Equal(TranslationStyle.Formal, Assert.Single(engine.LastRequest.Styles));
        Assert.Equal(TranslationStyle.Formal, Assert.Single(result.Artifacts).Style);
        Assert.Equal(
            TranslationStyle.Formal,
            Assert.Single(Assert.Single(workspace.AppliedTranslations.Entries).Variants).Style);
        var completed = Assert.IsType<PipelineProgress>(progressUpdates[^1]);
        Assert.Equal(PipelineStage.Completed, completed.Stage);
        Assert.Equal(1, completed.Fraction);
        Assert.Null(completed.NextStage);
        Assert.Null(completed.RollbackStatus);
        Assert.NotNull(completed.Stages);
        Assert.All(
            completed.Stages.Where(stage => stage.Stage is >= PipelineStage.Inspecting and <= PipelineStage.Committing),
            stage =>
            {
                Assert.Equal(PipelineStageStatus.Completed, stage.Status);
                Assert.NotNull(stage.StartedAtUtc);
                Assert.NotNull(stage.FinishedAtUtc);
            });
        Assert.Contains(
            progressUpdates,
            update => update.Stage == PipelineStage.Translating &&
                update.Message.Contains('1') &&
                update.NextStage == PipelineStage.Writing);
    }

    [Fact]
    public async Task SchedulerRetainsFinalProgressWhenAJobFinishesWithoutARegisteredSubscriber()
    {
        var pipeline = new TranslationPipeline(
            new StubWorkspaceBackend(new StubWorkspace()),
            new StubTranslationEngine(),
            new StubMemoryStore());
        await using var scheduler = new PipelineJobScheduler(pipeline);

        var handle = await scheduler.EnqueueAsync(
            CreateRequest(),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TestContext.Current.CancellationToken);

        var latest = Assert.IsType<PipelineProgress>(handle.LatestProgress);
        Assert.Equal(PipelineStage.Completed, latest.Stage);
        Assert.Equal(1, latest.Fraction);
        Assert.Null(latest.NextStage);
        Assert.NotNull(latest.Stages);
        Assert.Equal(9, latest.Stages.Count);
        Assert.All(latest.Stages, stage =>
            Assert.True(stage.Status is PipelineStageStatus.Completed or PipelineStageStatus.Skipped));

        var cancellationAttempts = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(handle.Cancel))
            .ToArray();
        await Task.WhenAll(cancellationAttempts);
        handle.Cancel();
    }

    [Fact]
    public async Task CancellationBeforeWorkspaceCreationDoesNotClaimARollbackOccurred()
    {
        var pipeline = new TranslationPipeline(
            new StubWorkspaceBackend(new StubWorkspace()),
            new StubTranslationEngine(),
            new StubMemoryStore());
        var progressUpdates = new List<PipelineProgress>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(
            CreateRequest(),
            new InlineProgress(progressUpdates.Add),
            cancellationToken: new CancellationToken(canceled: true)));

        var cancelled = progressUpdates[^1];
        Assert.Equal(PipelineStage.Cancelled, cancelled.Stage);
        Assert.Null(cancelled.RollbackStatus);
        Assert.DoesNotContain(cancelled.Stages ?? [], stage => stage.Stage == PipelineStage.RollingBack);
    }

    [Fact]
    public async Task ExecuteAsyncKeepsCommittedOutputWhenCacheSaveFails()
    {
        var workspace = new StubWorkspace();
        var memory = new StubMemoryStore { ThrowOnSave = true };
        memory.IsWorkspaceCommitted = () => workspace.Committed;
        var pipeline = new TranslationPipeline(
            new StubWorkspaceBackend(workspace),
            new StubTranslationEngine(),
            memory);

        var result = await pipeline.ExecuteAsync(
            CreateRequest(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(workspace.Committed);
        Assert.False(workspace.RolledBack);
        Assert.Single(result.Warnings);
        Assert.Contains("缓存未更新", result.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotReuseCacheAcrossModelSources()
    {
        var workspace = new StubWorkspace();
        var memory = new StubMemoryStore();
        var engine = new StubTranslationEngine();
        var pipeline = new TranslationPipeline(
            new StubWorkspaceBackend(workspace),
            engine,
            memory);

        var first = await pipeline.ExecuteAsync(
            CreateRequest("cloud-source-a"),
            cancellationToken: TestContext.Current.CancellationToken);
        var switched = await pipeline.ExecuteAsync(
            CreateRequest("cloud-source-b"),
            cancellationToken: TestContext.Current.CancellationToken);
        var repeated = await pipeline.ExecuteAsync(
            CreateRequest("cloud-source-b"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, first.TranslatedEntryCount);
        Assert.Equal(1, switched.TranslatedEntryCount);
        Assert.Equal(0, repeated.TranslatedEntryCount);
        Assert.Equal(1, repeated.ReusedEntryCount);
        Assert.Equal(2, engine.CallCount);
        Assert.Equal("cloud-source-b", memory.Saved?.Key.ModelSourceId);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotReuseCacheAcrossPromptContractVersions()
    {
        var workspace = new StubWorkspace();
        var memory = new StubMemoryStore();
        var versionOne = new StubTranslationEngine { TranslationContractVersion = "prompt-schema/v1" };
        var versionTwo = new StubTranslationEngine { TranslationContractVersion = "prompt-schema/v2" };

        var first = await new TranslationPipeline(
                new StubWorkspaceBackend(workspace),
                versionOne,
                memory)
            .ExecuteAsync(
                CreateRequest("cloud-source"),
                cancellationToken: TestContext.Current.CancellationToken);
        var upgraded = await new TranslationPipeline(
                new StubWorkspaceBackend(workspace),
                versionTwo,
                memory)
            .ExecuteAsync(
                CreateRequest("cloud-source"),
                cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, first.TranslatedEntryCount);
        Assert.Equal(1, upgraded.TranslatedEntryCount);
        Assert.Equal(1, versionOne.CallCount);
        Assert.Equal(1, versionTwo.CallCount);
        Assert.Equal("prompt-schema/v2", memory.Saved?.Key.TranslationContractVersion);
    }

    [Fact]
    public async Task ExecuteAsyncReusesDeterministicNullModelSourceCache()
    {
        var memory = new StubMemoryStore();
        var engine = new StubTranslationEngine();
        var pipeline = new TranslationPipeline(
            new StubWorkspaceBackend(new StubWorkspace()),
            engine,
            memory);

        await pipeline.ExecuteAsync(
            CreateRequest(),
            cancellationToken: TestContext.Current.CancellationToken);
        var repeated = await pipeline.ExecuteAsync(
            CreateRequest(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, engine.CallCount);
        Assert.Equal(1, repeated.ReusedEntryCount);
        Assert.Null(memory.Saved?.Key.ModelSourceId);
    }

    [Fact]
    public async Task SwitchingStylesMergesCacheVariantsWithoutRetranslatingTheFirstStyle()
    {
        var workspace = new StubWorkspace();
        var memory = new StubMemoryStore();
        var engine = new StubTranslationEngine();
        var pipeline = new TranslationPipeline(
            new StubWorkspaceBackend(workspace),
            engine,
            memory);

        var firstFormal = await pipeline.ExecuteAsync(
            CreateRequest(translationStyle: TranslationStyle.Formal),
            cancellationToken: TestContext.Current.CancellationToken);
        var informal = await pipeline.ExecuteAsync(
            CreateRequest(translationStyle: TranslationStyle.Informal),
            cancellationToken: TestContext.Current.CancellationToken);
        var repeatedFormal = await pipeline.ExecuteAsync(
            CreateRequest(translationStyle: TranslationStyle.Formal),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, firstFormal.TranslatedEntryCount);
        Assert.Equal(1, informal.TranslatedEntryCount);
        Assert.Equal(0, repeatedFormal.TranslatedEntryCount);
        Assert.Equal(1, repeatedFormal.ReusedEntryCount);
        Assert.Equal(2, engine.CallCount);
        Assert.Equal(TranslationStyle.Formal, Assert.Single(firstFormal.Artifacts).Style);
        Assert.Equal(TranslationStyle.Informal, Assert.Single(informal.Artifacts).Style);
        Assert.Equal(TranslationStyle.Formal, Assert.Single(repeatedFormal.Artifacts).Style);
        Assert.NotNull(memory.Saved);
        Assert.Equal(
            [TranslationStyle.Formal, TranslationStyle.Informal],
            Assert.Single(memory.Saved.Entries).Value.Variants.Select(static variant => variant.Style));
    }

    [Fact]
    public async Task ExecuteAsyncTranslatesVerifiedExternalizedEntriesInSameBatch()
    {
        var workspace = new StubWorkspace { ReturnVerifiedSafeCandidate = true };
        var engine = new StubTranslationEngine();
        var pipeline = new TranslationPipeline(
            new StubWorkspaceBackend(workspace),
            engine,
            new StubMemoryStore());

        var result = await pipeline.ExecuteAsync(
            CreateRequest(hardcodedStringMode: HardcodedStringMode.ExternalizeRecognizedSafePatterns),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.SourceEntryCount);
        Assert.Equal(2, result.TranslatedEntryCount);
        Assert.Equal(1, result.Externalization.ExternalizedCount);
        Assert.True(workspace.Externalized);
        Assert.True(workspace.ExternalizeOrder < workspace.ReadEntriesOrder);
        Assert.Contains(
            workspace.AppliedTranslations!.Entries,
            static entry => entry.Key == "example.hardcoded.open_settings");
    }

    [Fact]
    public async Task ExecuteAsyncRollsBackWhenVerifiedExternalizationFails()
    {
        var workspace = new StubWorkspace
        {
            ReturnVerifiedSafeCandidate = true,
            ThrowOnExternalize = true
        };
        var pipeline = new TranslationPipeline(
            new StubWorkspaceBackend(workspace),
            new StubTranslationEngine(),
            new StubMemoryStore());

        await Assert.ThrowsAsync<PipelineException>(
            () => pipeline.ExecuteAsync(
                CreateRequest(hardcodedStringMode: HardcodedStringMode.ExternalizeRecognizedSafePatterns),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(workspace.RolledBack);
        Assert.False(workspace.Committed);
        Assert.False(workspace.Externalized);
    }

    [Fact]
    public async Task ExecuteAsyncRollsBackWhenVerificationFails()
    {
        var workspace = new StubWorkspace
        {
            Verification = new PackageVerification(false, true, VerificationErrors)
        };
        var pipeline = new TranslationPipeline(
            new StubWorkspaceBackend(workspace),
            new StubTranslationEngine(),
            new StubMemoryStore());
        var progressUpdates = new List<PipelineProgress>();

        var exception = await Assert.ThrowsAsync<PipelineException>(
            () => pipeline.ExecuteAsync(
                CreateRequest(),
                new InlineProgress(progressUpdates.Add),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(PipelineStage.Verifying, exception.FailedStage);
        Assert.True(workspace.RolledBack);
        Assert.False(workspace.Committed);
        var failed = progressUpdates[^1];
        Assert.Equal(PipelineStage.Failed, failed.Stage);
        Assert.Equal(PipelineStageStatus.Completed, failed.RollbackStatus);
        Assert.NotNull(failed.Stages);
        Assert.Equal(
            PipelineStageStatus.Failed,
            Assert.Single(failed.Stages, stage => stage.Stage == PipelineStage.Verifying).Status);
        var rollback = Assert.Single(failed.Stages, stage => stage.Stage == PipelineStage.RollingBack);
        Assert.NotNull(rollback.StartedAtUtc);
        Assert.NotNull(rollback.FinishedAtUtc);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsMoreThanTheOneSelectedStyleArtifact()
    {
        var workspace = new StubWorkspace
        {
            Verification = new PackageVerification(
                true,
                true,
                [],
                [
                    new PackageArtifact(TranslationStyle.Formal, "formal.jar"),
                    new PackageArtifact(TranslationStyle.Informal, "informal.jar")
                ])
        };
        var pipeline = new TranslationPipeline(
            new StubWorkspaceBackend(workspace),
            new StubTranslationEngine(),
            new StubMemoryStore());

        var exception = await Assert.ThrowsAsync<PipelineException>(() => pipeline.ExecuteAsync(
            CreateRequest(),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(PipelineStage.Verifying, exception.FailedStage);
        Assert.False(workspace.Committed);
        Assert.True(workspace.RolledBack);
    }

    [Fact]
    public async Task ExecuteAsyncBlocksSignedArchiveByDefault()
    {
        var workspace = new StubWorkspace
        {
            Inspection = new ArchiveInspection(
                "signed-package",
                "signed",
                "forge",
                false,
                ArchiveSignatureState.PresentUnverified,
                false,
                Array.Empty<string>())
        };
        var pipeline = new TranslationPipeline(
            new StubWorkspaceBackend(workspace),
            new StubTranslationEngine(),
            new StubMemoryStore());

        var exception = await Assert.ThrowsAsync<PipelineException>(
            () => pipeline.ExecuteAsync(
                CreateRequest(),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.True(workspace.RolledBack);
        Assert.False(workspace.Extracted);
    }

    private static PipelineRequest CreateRequest(
        string? modelSourceId = null,
        HardcodedStringMode hardcodedStringMode = HardcodedStringMode.ScanOnly,
        TranslationStyle translationStyle = TranslationStyle.Formal)
    {
        var prefix = Path.Combine(Path.GetTempPath(), "localesmith-tests");
        return new PipelineRequest(
            Path.Combine(prefix, "input.jar"),
            Path.Combine(prefix, "output.jar"),
            styles: new HashSet<TranslationStyle> { translationStyle },
            hardcodedStringMode: hardcodedStringMode,
            modelSourceId: modelSourceId);
    }

    private sealed class StubWorkspaceBackend(StubWorkspace workspace) : IArchiveWorkspaceBackend
    {
        public Task<IArchiveWorkspace> BeginAsync(
            Guid jobId,
            PipelineRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IArchiveWorkspace>(workspace);
        }
    }

    private sealed class StubWorkspace : IArchiveWorkspace
    {
        public ArchiveInspection Inspection { get; init; } = new(
            "example-package",
            "example",
            "fabric",
            false,
            ArchiveSignatureState.None,
            false,
            Array.Empty<string>());

        public PackageVerification Verification { get; init; } = new(true, true, Array.Empty<string>());

        public bool Extracted { get; private set; }

        public bool Committed { get; private set; }

        public bool RolledBack { get; private set; }

        public TranslationBatchResult? AppliedTranslations { get; private set; }

        public bool ReturnVerifiedSafeCandidate { get; init; }

        public bool ThrowOnExternalize { get; init; }

        public bool Externalized { get; private set; }

        public int ExternalizeOrder { get; private set; }

        public int ReadEntriesOrder { get; private set; }

        private int OperationOrder { get; set; }

        public Task<ArchiveInspection> InspectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Inspection);

        public Task ExtractAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Extracted = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranslationEntry>> ReadTranslatableEntriesAsync(
            CancellationToken cancellationToken)
        {
            ReadEntriesOrder = ++OperationOrder;
            var entries = new List<TranslationEntry>
            {
                new("assets/example/lang/en_us.json", "item.example", "Example")
            };
            if (Externalized)
            {
                entries.Add(new TranslationEntry(
                    "example/Screen.class",
                    "example.hardcoded.open_settings",
                    "Open settings"));
            }

            return Task.FromResult<IReadOnlyList<TranslationEntry>>(entries);
        }

        public Task<IReadOnlyList<HardcodedStringCandidate>> ScanHardcodedStringsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HardcodedStringCandidate>>(
                ReturnVerifiedSafeCandidate
                    ? new[]
                    {
                        new HardcodedStringCandidate(
                            2,
                            "example/Screen.class",
                            "example/Screen",
                            "render",
                            "()V",
                            4,
                            "ldc",
                            7,
                            "Open settings",
                            "example.hardcoded.open_settings",
                            true)
                    }
                    : Array.Empty<HardcodedStringCandidate>());

        public Task<ExternalizationReport> ExternalizeAsync(
            IReadOnlyList<HardcodedStringCandidate> candidates,
            CancellationToken cancellationToken)
        {
            ExternalizeOrder = ++OperationOrder;
            if (ThrowOnExternalize)
            {
                throw new InvalidDataException("simulated classfile rewrite failure");
            }

            Externalized = candidates.Count > 0;
            return Task.FromResult(new ExternalizationReport(candidates.Count, candidates.Count, Array.Empty<string>()));
        }

        public Task ApplyTranslationsAsync(
            TranslationBatchResult translations,
            CancellationToken cancellationToken)
        {
            AppliedTranslations = translations;
            return Task.CompletedTask;
        }

        public Task<PackageVerification> StagePackageAsync(
            string outputPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(Verification);

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            RolledBack = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubTranslationEngine : ITranslationEngine
    {
        public string TranslationContractVersion { get; init; } = TranslationPromptContract.CurrentVersion;

        public int CallCount { get; private set; }

        public TranslationBatchRequest? LastRequest { get; private set; }

        public Task<TranslationBatchResult> TranslateAsync(
            TranslationBatchRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            var entries = request.Entries.Select(entry => new TranslatedEntry(
                entry.RelativePath,
                entry.Key,
                IncrementalTranslationPlanner.ComputeHash(entry),
                request.Styles
                    .Select(style => new TranslationVariant(style, $"{style}:{entry.SourceText}"))
                    .ToArray())).ToArray();
            return Task.FromResult(new TranslationBatchResult(request.TargetLanguage, entries));
        }
    }

    private sealed class InlineProgress(Action<PipelineProgress> report) : IProgress<PipelineProgress>
    {
        public void Report(PipelineProgress value) => report(value);
    }

    private sealed class StubMemoryStore : ITranslationMemoryStore
    {
        private readonly Dictionary<TranslationMemoryKey, TranslationMemorySnapshot> _snapshots = new();

        public TranslationMemorySnapshot? Saved { get; private set; }

        public bool ThrowOnSave { get; init; }

        public Func<bool>? IsWorkspaceCommitted { get; set; }

        public bool WorkspaceWasCommittedWhenSaved { get; private set; }

        public Task<TranslationMemorySnapshot> LoadAsync(
            TranslationMemoryKey key,
            CancellationToken cancellationToken)
        {
            var normalized = key.Normalize();
            return Task.FromResult(
                _snapshots.TryGetValue(normalized, out var snapshot)
                    ? snapshot
                    : TranslationMemorySnapshot.Empty(normalized));
        }

        public Task SaveAsync(
            TranslationMemorySnapshot snapshot,
            CancellationToken cancellationToken)
        {
            WorkspaceWasCommittedWhenSaved = IsWorkspaceCommitted?.Invoke() ?? true;
            if (ThrowOnSave)
            {
                throw new IOException("simulated cache failure");
            }

            Saved = snapshot;
            _snapshots[snapshot.Key.Normalize()] = snapshot;
            return Task.CompletedTask;
        }
    }
}
