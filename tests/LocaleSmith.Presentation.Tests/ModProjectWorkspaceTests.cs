using LocaleSmith.Application.Models;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Models;
using LocaleSmith.Presentation.Services;

namespace LocaleSmith.Presentation.Tests;

public sealed class ModProjectWorkspaceTests
{
    [Fact]
    public void NormalizedSourceHasStableProcessLifetimeProjectIdAndPublishesChanges()
    {
        var workspace = new InMemoryModProjectWorkspace();
        var changes = new List<ModProjectWorkspaceChangeKind>();
        workspace.Changed += (_, args) => changes.Add(args.Kind);
        string source = Path.Combine(Path.GetTempPath(), "LocaleSmith", "projects", "example.jar");

        ModProjectSnapshot first = workspace.RegisterProject(source);
        ModProjectSnapshot second = workspace.RegisterProject(
            Path.Combine(Path.GetDirectoryName(source)!, ".", Path.GetFileName(source)));

        Assert.Equal(first.ProjectId, second.ProjectId);
        Assert.Equal(first.ProjectId, workspace.ActiveProject?.ProjectId);
        Assert.Single(workspace.Projects);
        Assert.Contains(ModProjectWorkspaceChangeKind.ProjectRegistered, changes);
        Assert.Contains(ModProjectWorkspaceChangeKind.ActiveProjectChanged, changes);
    }

    [Fact]
    public void TaskLifecycleCapturesRealJobProgressResultAndCancellationHandle()
    {
        var workspace = new InMemoryModProjectWorkspace();
        ModProjectSnapshot project = workspace.RegisterProject(
            Path.Combine(Path.GetTempPath(), "LocaleSmith", "projects", "lifecycle.jar"));
        ModProjectTaskSnapshot registered = workspace.RegisterTask(
            project.ProjectId,
            CreateRegistration(project.SourceArtifactPath));
        var cancelled = false;
        Guid jobId = Guid.NewGuid();

        ModProjectTaskSnapshot queued = workspace.AttachJob(
            registered.TaskId,
            jobId,
            () => cancelled = true);
        Assert.Equal(ModProjectTaskStatus.Queued, queued.Status);
        Assert.Equal(jobId, queued.JobId);

        Assert.True(workspace.TryReportProgress(
            jobId,
            new TranslationQueueProgress(jobId, PipelineStage.Translating, 0.45),
            out ModProjectTaskSnapshot? running));
        Assert.Equal(ModProjectTaskStatus.Running, running?.Status);
        Assert.Equal(PipelineStage.Translating, running?.Stage);
        Assert.Equal(0.45, running?.Progress);

        Assert.True(workspace.TryRequestCancellation(registered.TaskId, out ModProjectTaskSnapshot? cancelling));
        Assert.True(cancelled);
        Assert.Equal(ModProjectTaskStatus.CancellationRequested, cancelling?.Status);
        Assert.True(workspace.TryMarkCancelled(registered.TaskId, out ModProjectTaskSnapshot? cancelledTask));
        Assert.Equal(ModProjectTaskStatus.Cancelled, cancelledTask?.Status);
        Assert.False(workspace.TryRequestCancellation(registered.TaskId, out _));
    }

    [Fact]
    public void CompletionUpdatesBothTaskAndProjectWithImmutableArtifactSnapshot()
    {
        var workspace = new InMemoryModProjectWorkspace();
        ModProjectSnapshot project = workspace.RegisterProject(
            Path.Combine(Path.GetTempPath(), "LocaleSmith", "projects", "completed.jar"));
        ModProjectTaskSnapshot task = workspace.RegisterTask(
            project.ProjectId,
            CreateRegistration(project.SourceArtifactPath));
        Guid jobId = Guid.NewGuid();
        workspace.AttachJob(task.TaskId, jobId, static () => { });
        string[] artifacts = [task.OutputPath];
        var usage = new ModelTokenUsage(120, 40, 160, 2, 2, 2);

        Assert.True(workspace.TryCompleteTask(
            task.TaskId,
            new TranslationQueueResult(
                jobId,
                artifacts[0],
                "examplemod",
                "Fabric",
                artifacts,
                [],
                0,
                ModelUsage: usage),
            out ModProjectTaskSnapshot? completed));
        artifacts[0] = "mutated.jar";

        Assert.Equal(ModProjectTaskStatus.Completed, completed?.Status);
        Assert.Equal("examplemod", workspace.ActiveProject?.ModId);
        Assert.Equal("Fabric", workspace.ActiveProject?.Loader);
        Assert.EndsWith("translated.jar", Assert.Single(completed!.ArtifactPaths), StringComparison.Ordinal);
        Assert.Same(usage, completed.ModelUsage);
        Assert.False(workspace.TryReportProgress(
            jobId,
            new TranslationQueueProgress(jobId, PipelineStage.Translating, 0.1),
            out _));
    }

    [Fact]
    public void ConcurrentRegistrationOfOneArtifactCreatesOneProject()
    {
        var workspace = new InMemoryModProjectWorkspace();
        string source = Path.Combine(Path.GetTempPath(), "LocaleSmith", "projects", "parallel.jar");
        var ids = new Guid[64];

        Parallel.For(0, ids.Length, index => ids[index] = workspace.RegisterProject(source).ProjectId);

        Assert.Single(ids.Distinct());
        Assert.Single(workspace.Projects);
    }

    [Fact]
    public void TaskCannotSubstituteAnotherProjectsHostPath()
    {
        var workspace = new InMemoryModProjectWorkspace();
        ModProjectSnapshot project = workspace.RegisterProject(
            Path.Combine(Path.GetTempPath(), "LocaleSmith", "projects", "registered.jar"));
        ModProjectTaskRegistration substituted = CreateRegistration(
            Path.Combine(Path.GetTempPath(), "LocaleSmith", "projects", "different.jar"));

        Assert.Throws<ArgumentException>(() => workspace.RegisterTask(project.ProjectId, substituted));
    }

    private static ModProjectTaskRegistration CreateRegistration(string sourcePath) => new(
        sourcePath,
        Path.Combine(Path.GetTempPath(), "LocaleSmith", "output", "translated.jar"),
        "model-source",
        "zh_CN",
        TranslationStyle.Formal,
        "Translate the active mod into Simplified Chinese.");
}
