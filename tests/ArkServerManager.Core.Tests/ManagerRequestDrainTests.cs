using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ManagerRequestDrainTests
{
    [Fact]
    public async Task ShutdownRefusesOtherRequestsAndBlocksNewWorkAfterDraining()
    {
        await using var operations = new BackgroundOperations(4, 1);
        var drain = new ManagerRequestDrain();
        using var shutdownRequest = drain.Enter();
        using (var other = drain.Enter()) Assert.False(drain.TryBegin(operations));
        Assert.True(drain.TryBegin(operations));
        Assert.Throws<InvalidOperationException>(() => drain.Enter());
        Assert.Throws<InvalidOperationException>(() => operations.Submit(Guid.NewGuid(), "late", null, (_, _) => Task.FromResult<object>(new { })));
    }

    [Fact]
    public async Task ShutdownWaitsForJobAndCompletionReportingWithoutCancelingThem()
    {
        var reportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var operations = new BackgroundOperations(4, 1, async _ => { reportStarted.SetResult(); await reportFinish.Task; });
        var drain = new ManagerRequestDrain();
        using var shutdownRequest = drain.Enter();
        operations.Submit(Guid.NewGuid(), "work", null, (_, _) => Task.FromResult<object>(new { Done = true }));
        await reportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(drain.TryBegin(operations));
        using (drain.Enter()) { }
        reportFinish.SetResult();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!drain.TryBegin(operations)) await Task.Delay(10, deadline.Token);
        Assert.Throws<InvalidOperationException>(() => drain.Enter());
    }
}
