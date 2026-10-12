using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class GracefulHostShutdownTests
{
    [Fact]
    public async Task SuccessFlagWithRunningStateCannotConfirmShutdown()
    {
        var result = await GracefulHostShutdown.RunAsync(_ => Task.FromResult(new LifecycleOperationResult([
            new(Guid.NewGuid(), ServerState.Running, true, null)])), (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));
        Assert.False(result.Stopped); Assert.True(result.NoticeRecorded);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReportsConfirmedAndUnconfirmedShutdownWithoutInventingSuccess(bool success)
    {
        ActivitySeverity? severity = null;
        var result = await GracefulHostShutdown.RunAsync(_ => Task.FromResult(new LifecycleOperationResult([
            new(Guid.NewGuid(), success ? ServerState.Stopped : ServerState.Running, success, success ? null : "Not confirmed")])),
            (level, _) => { severity = level; return Task.CompletedTask; }, TimeSpan.FromSeconds(1));
        Assert.Equal(success, result.Stopped); Assert.False(result.TimedOut); Assert.True(result.NoticeRecorded);
        Assert.Equal(success ? ActivitySeverity.Information : ActivitySeverity.Error, severity);
    }
    [Fact]
    public async Task DeadlineDoesNotClaimServersStopped()
    {
        var result = await GracefulHostShutdown.RunAsync(async ct =>
        { await Task.Delay(Timeout.Infinite, ct); return new([]); }, (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(30));
        Assert.False(result.Stopped); Assert.True(result.TimedOut); Assert.True(result.NoticeRecorded);
    }
    [Fact]
    public async Task LogFailureDoesNotChangeConfirmedServerOutcome()
    {
        var result = await GracefulHostShutdown.RunAsync(_ => Task.FromResult(new LifecycleOperationResult([])),
            (_, _) => throw new IOException("Log unavailable"), TimeSpan.FromSeconds(1));
        Assert.True(result.Stopped); Assert.False(result.NoticeRecorded);
    }
}
