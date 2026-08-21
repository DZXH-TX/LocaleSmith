using LocaleSmith.App.Services;

namespace LocaleSmith.App.Tests;

public sealed class NavigationInitializationCoordinatorTests
{
    [Fact]
    public async Task ReenterDuringCancellationWaitsThenRunsOneReplacement()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var coordinator = new NavigationInitializationCoordinator();
        var firstRunStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRun = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;
        var concurrentRuns = 0;

        async Task<bool> InitializeAsync()
        {
            var invocation = Interlocked.Increment(ref invocationCount);
            Assert.Equal(1, Interlocked.Increment(ref concurrentRuns));
            try
            {
                if (invocation == 1)
                {
                    firstRunStarted.TrySetResult();
                    await releaseFirstRun.Task.WaitAsync(cancellationToken);
                    return false;
                }

                return true;
            }
            finally
            {
                Interlocked.Decrement(ref concurrentRuns);
            }
        }

        var firstActivation = coordinator.ActivateAsync(InitializeAsync);
        await firstRunStarted.Task.WaitAsync(cancellationToken);

        coordinator.Deactivate();
        var secondActivation = coordinator.ActivateAsync(InitializeAsync);

        Assert.Equal(1, Volatile.Read(ref invocationCount));
        releaseFirstRun.TrySetResult();
        await Task.WhenAll(firstActivation, secondActivation).WaitAsync(cancellationToken);

        Assert.Equal(2, invocationCount);
        Assert.Equal(0, concurrentRuns);
        Assert.True(coordinator.IsInitialized);
    }

    [Fact]
    public async Task DuplicateActivationInSameNavigationDoesNotRetryFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var coordinator = new NavigationInitializationCoordinator();
        var runStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRun = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;

        async Task<bool> InitializeAsync()
        {
            Interlocked.Increment(ref invocationCount);
            runStarted.TrySetResult();
            await releaseRun.Task.WaitAsync(cancellationToken);
            return false;
        }

        var firstActivation = coordinator.ActivateAsync(InitializeAsync);
        await runStarted.Task.WaitAsync(cancellationToken);
        var duplicateActivation = coordinator.ActivateAsync(InitializeAsync);

        releaseRun.TrySetResult();
        await Task.WhenAll(firstActivation, duplicateActivation).WaitAsync(cancellationToken);

        Assert.Equal(1, invocationCount);
        Assert.False(coordinator.IsInitialized);
    }

    [Fact]
    public async Task SuccessAfterLeavingDoesNotInitializeInactivePage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var coordinator = new NavigationInitializationCoordinator();
        var firstRunStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRun = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;

        async Task<bool> InitializeAsync()
        {
            var invocation = Interlocked.Increment(ref invocationCount);
            if (invocation == 1)
            {
                firstRunStarted.TrySetResult();
                await releaseFirstRun.Task.WaitAsync(cancellationToken);
            }

            return true;
        }

        var firstActivation = coordinator.ActivateAsync(InitializeAsync);
        await firstRunStarted.Task.WaitAsync(cancellationToken);
        coordinator.Deactivate();
        releaseFirstRun.TrySetResult();
        await firstActivation.WaitAsync(cancellationToken);

        Assert.False(coordinator.IsInitialized);

        await coordinator.ActivateAsync(InitializeAsync).WaitAsync(cancellationToken);

        Assert.Equal(2, invocationCount);
        Assert.True(coordinator.IsInitialized);
    }
}
