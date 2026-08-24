using LocaleSmith.App.Services;
using LocaleSmith.Application.Models;
using LocaleSmith.Archive;
using LocaleSmith.Core.Models;
using LocaleSmith.Mcp;
using LocaleSmith.NativeInterop;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.Services;

namespace LocaleSmith.App.Tests;

public sealed class ProjectMcpBackendTests
{
    [Fact]
    public async Task InspectUsesOnlyActiveProjectsRegisteredArtifactAndUpdatesWorkspace()
    {
        string source = Path.GetTempFileName();
        try
        {
            var workspace = new InMemoryModProjectWorkspace();
            ModProjectSnapshot project = workspace.RegisterProject(source);
            var scanner = new RecordingScanner(CreateManifest(source));
            using var backend = CreateBackend(workspace, scanner, out _, out _);

            ArchiveMcpInspection inspection = await backend.InspectArchiveAsync(
                project.ProjectId,
                TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath(source), scanner.LastPath);
            Assert.Equal("examplemod", inspection.ModId);
            Assert.Equal("fabric", inspection.Loader);
            Assert.Equal((ulong)3, inspection.EntryCount);
            Assert.Equal("examplemod", workspace.ActiveProject?.ModId);
            await Assert.ThrowsAsync<ProjectMcpBackendException>(() =>
                backend.InspectArchiveAsync(Guid.NewGuid(), TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task StartRunsRealQueueHandleRejectsDuplicateAndCancelUsesBoundHandle()
    {
        string source = Path.GetTempFileName();
        try
        {
            var workspace = new InMemoryModProjectWorkspace();
            ModProjectSnapshot project = workspace.RegisterProject(source);
            using var backend = CreateBackend(
                workspace,
                new RecordingScanner(CreateManifest(source)),
                out ControllableQueue queue,
                out _);
            var request = new TranslationMcpStartRequest(
                project.ProjectId,
                "Translate the selected mod into Japanese.",
                TargetLanguage: "ja_JP",
                Style: "informal");

            TaskMcpSnapshot started = await backend.StartTranslationAsync(
                request,
                TestContext.Current.CancellationToken);

            Assert.Equal("Queued", started.Status);
            Assert.Equal("ja_JP", queue.Request?.TargetLanguage);
            Assert.Equal(TranslationStyle.Informal, queue.Request?.Style);
            await Assert.ThrowsAsync<ProjectMcpBackendException>(() =>
                backend.StartTranslationAsync(request, TestContext.Current.CancellationToken).AsTask());

            queue.Report(PipelineStage.Translating, 0.4);
            TaskMcpSnapshot? running = await backend.GetTaskAsync(
                started.TaskId,
                TestContext.Current.CancellationToken);
            Assert.Equal("Running", running?.Status);
            Assert.Equal("Translating", running?.Stage);

            TaskMcpSnapshot cancelling = await backend.CancelTaskAsync(
                started.TaskId,
                TestContext.Current.CancellationToken);
            Assert.Equal("CancellationRequested", cancelling.Status);
            Assert.True(queue.CancelCalled);
            await WaitUntilAsync(
                async () => (await backend.GetTaskAsync(started.TaskId))?.Status == "Cancelled",
                TestContext.Current.CancellationToken);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task CompletionPublishesArtifactsAndProviderReportedUsage()
    {
        string source = Path.GetTempFileName();
        try
        {
            var workspace = new InMemoryModProjectWorkspace();
            ModProjectSnapshot project = workspace.RegisterProject(source);
            using var backend = CreateBackend(
                workspace,
                new RecordingScanner(CreateManifest(source)),
                out ControllableQueue queue,
                out string outputPath);
            TaskMcpSnapshot started = await backend.StartTranslationAsync(
                new TranslationMcpStartRequest(project.ProjectId, "Translate the selected mod."),
                TestContext.Current.CancellationToken);
            var usage = new ModelTokenUsage(120, 30, 150, 2, 2, 2);

            queue.Complete(new TranslationQueueResult(
                started.JobId!.Value,
                outputPath,
                "examplemod",
                "fabric",
                [outputPath],
                [],
                0,
                ModelUsage: usage));
            await WaitUntilAsync(
                async () => (await backend.GetTaskAsync(started.TaskId))?.Status == "Completed",
                TestContext.Current.CancellationToken);
            TaskMcpSnapshot completed = Assert.IsType<TaskMcpSnapshot>(
                await backend.GetTaskAsync(started.TaskId, TestContext.Current.CancellationToken));

            Assert.Equal(120, completed.InputTokens);
            Assert.Equal(30, completed.OutputTokens);
            Assert.Equal(150, completed.TotalTokens);
            Assert.Equal(2, completed.ProviderCallCount);
            Assert.True(completed.UsageComplete);
            Assert.Equal(Path.GetFileName(outputPath), Assert.Single(completed.ArtifactNames));
        }
        finally
        {
            File.Delete(source);
        }
    }

    private static ProjectMcpBackend CreateBackend(
        IModProjectWorkspace workspace,
        IArchiveScanner scanner,
        out ControllableQueue queue,
        out string outputPath)
    {
        queue = new ControllableQueue();
        outputPath = Path.Combine(Path.GetTempPath(), "LocaleSmith", "project-mcp-tests", "translated.jar");
        var source = new ModelSource(
            "model-source",
            "Model source",
            ModelProviderKind.Ollama,
            new Uri("http://127.0.0.1:11434"),
            "llama3");
        return new ProjectMcpBackend(
            workspace,
            scanner,
            queue,
            new FixedOutputPath(outputPath),
            new FixedModelSelection(source));
    }

    private static ArchiveScanManifest CreateManifest(string sourcePath) => new()
    {
        SchemaVersion = 1,
        CoreVersion = "test",
        Source = new NativeSourceArchive
        {
            Path = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            SizeBytes = 1
        },
        Archive = new NativeArchiveInventory
        {
            EntryCount = 3,
            TotalCompressedBytes = 1,
            TotalUncompressedBytes = 1,
            Entries = [],
            Signatures = new NativeSignatureEvidence
            {
                Status = "none",
                ManifestPresent = false,
                SignatureFiles = [],
                SignatureBlocks = [],
                CryptographicallyVerified = false,
                ModificationBlockedByDefault = false,
                RepackWarning = "none"
            }
        },
        ModMetadata = new NativeModMetadata
        {
            DetectionPrecedence = [],
            PrimaryLoader = "fabric",
            PrimaryModId = "examplemod",
            ModIds = ["examplemod"],
            UsedFilenameFallback = false,
            FilenameFallbackNamespace = "example"
        },
        Resources =
        [
            new NativeResourceEntry
            {
                ArchiveIndex = 0,
                Path = "assets/examplemod/lang/en_us.json",
                Kind = "language_json",
                Crc32 = 0,
                CompressedSizeBytes = 1,
                UncompressedSizeBytes = 1
            }
        ],
        Warnings = []
    };

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await condition())
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException("The project task did not reach the expected state.");
    }

    private sealed class RecordingScanner(ArchiveScanManifest manifest) : IArchiveScanner
    {
        public string? LastPath { get; private set; }

        public ArchiveScanManifest ScanArchive(string archivePath)
        {
            LastPath = archivePath;
            return manifest;
        }
    }

    private sealed class ControllableQueue : ITranslationQueueService
    {
        private readonly TaskCompletionSource<TranslationQueueResult> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private TranslationQueueProgress? _latestProgress;

        public event EventHandler<TranslationQueueProgress>? ProgressChanged;

        public TranslationQueueRequest? Request { get; private set; }

        public Guid JobId { get; private set; }

        public bool CancelCalled { get; private set; }

        public ValueTask<TranslationQueueHandle> EnqueueAsync(
            TranslationQueueRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            JobId = Guid.NewGuid();
            _latestProgress = new TranslationQueueProgress(JobId, PipelineStage.Queued, 0);
            return ValueTask.FromResult(new TranslationQueueHandle(
                JobId,
                _completion.Task,
                Cancel,
                () => _latestProgress));
        }

        public void Report(PipelineStage stage, double progress)
        {
            _latestProgress = new TranslationQueueProgress(JobId, stage, progress);
            ProgressChanged?.Invoke(this, _latestProgress);
        }

        public void Complete(TranslationQueueResult result) => _completion.TrySetResult(result);

        private void Cancel()
        {
            CancelCalled = true;
            _completion.TrySetCanceled(new CancellationToken(canceled: true));
        }
    }

    private sealed class FixedOutputPath(string outputPath) : IOutputPathStrategy
    {
        public Task<string> CreateOutputPathAsync(
            string sourcePath,
            string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(outputPath);
        }
    }

    private sealed class FixedModelSelection : IModelSelectionService
    {
        private readonly ModelSource _source;

        public FixedModelSelection(ModelSource source)
        {
            _source = source;
            Sources = [source];
            SelectedSource = source;
        }

        public IReadOnlyList<ModelSource> Sources { get; }

        public ModelSource? SelectedSource { get; }

        public Task<bool> SelectSourceAsync(
            string sourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(sourceId, _source.Id, StringComparison.Ordinal));
    }
}
