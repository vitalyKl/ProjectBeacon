namespace ProjectBeacon.Cli.Tests.ModelSwapping;

using ProjectBeacon.Cli.Client.ModelSwapping;

public sealed class ModelExecutionSchedulerTests
{
    [Fact]
    public async Task ExclusiveModels_ShareOneFifoGenerationSlot()
    {
        var scheduler = new ModelExecutionScheduler();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = scheduler.RunModelRequestAsync(false, async () =>
        {
            firstStarted.TrySetResult(true);
            await releaseFirst.Task.WaitAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var second = scheduler.RunModelRequestAsync(false, () =>
        {
            secondStarted.TrySetResult(true);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.False(secondStarted.Task.IsCompleted);
        releaseFirst.TrySetResult(true);

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(secondStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task ConcurrentResidentModel_CanRunBesideExclusiveSwapModel()
    {
        var scheduler = new ModelExecutionScheduler();
        var exclusiveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var residentStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExclusive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResident = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var exclusive = scheduler.RunModelRequestAsync(false, async () =>
        {
            exclusiveStarted.TrySetResult(true);
            await releaseExclusive.Task.WaitAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await exclusiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var resident = scheduler.RunModelRequestAsync(true, async () =>
        {
            residentStarted.TrySetResult(true);
            await releaseResident.Task.WaitAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await residentStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        releaseResident.TrySetResult(true);
        releaseExclusive.TrySetResult(true);

        await Task.WhenAll(exclusive, resident).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResumedExclusiveSession_RejoinsAtTailAfterWaitingForUser()
    {
        var scheduler = new ModelExecutionScheduler();
        var order = new List<string>();
        var secondTurnEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondTurn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The first generation finishes and the agent then waits for an answer outside the
        // scheduler. Its model slot is released while the form is pending.
        await scheduler.RunModelRequestAsync(false, () =>
        {
            order.Add("A:first-generation");
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        // Another exclusive model takes the free slot while A is waiting for the user.
        var secondTurn = scheduler.RunModelRequestAsync(false, async () =>
        {
            order.Add("B:generation");
            secondTurnEntered.TrySetResult(true);
            await releaseSecondTurn.Task.WaitAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await secondTurnEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        // When the answer arrives, A requests a new generation slot and joins behind B.
        var resumedFirstTurn = scheduler.RunModelRequestAsync(false, () =>
        {
            order.Add("A:resumed-generation");
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "A:first-generation", "B:generation" }, order);
        releaseSecondTurn.TrySetResult(true);

        await Task.WhenAll(secondTurn, resumedFirstTurn).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "A:first-generation", "B:generation", "A:resumed-generation" }, order);
    }

    [Fact]
    public async Task MaintenanceWaitsForResidentAndSwapModelRequests()
    {
        var scheduler = new ModelExecutionScheduler();
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenanceStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var request = scheduler.RunModelRequestAsync(true, async () =>
        {
            requestStarted.TrySetResult(true);
            await releaseRequest.Task.WaitAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var maintenance = scheduler.RunMaintenanceAsync(() =>
        {
            maintenanceStarted.TrySetResult(true);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.False(maintenanceStarted.Task.IsCompleted);
        releaseRequest.TrySetResult(true);

        await Task.WhenAll(request, maintenance).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(maintenanceStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task HeartbeatMaintenance_DoesNotWaitForAnActiveModelGeneration()
    {
        var scheduler = new ModelExecutionScheduler();
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenanceStarted = false;

        var request = scheduler.RunModelRequestAsync(false, async () =>
        {
            requestStarted.TrySetResult(true);
            await releaseRequest.Task.WaitAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var synced = await scheduler.TryRunMaintenanceAsync(() =>
        {
            maintenanceStarted = true;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.False(synced);
        Assert.False(maintenanceStarted);

        releaseRequest.TrySetResult(true);
        await request.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        synced = await scheduler.TryRunMaintenanceAsync(() =>
        {
            maintenanceStarted = true;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.True(synced);
        Assert.True(maintenanceStarted);
    }
}
