using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ManagerOwnershipLifecycleTests
{
    [Fact]
    public async Task OwnedWorkStartsUnderLeaseAndCleanupKeepsLeaseUntilComplete()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-manager-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var file = Path.Combine(root, "manager-host.lock");
        var server = new LocalManagerServer(root, (_, _) => Task.FromResult<object>(new { }),
            async () => { cleaning.TrySetResult(); await release.Task; },
            afterOwnershipAcquired: _ =>
            {
                Assert.Throws<IOException>(() => new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                started.TrySetResult(); return Task.CompletedTask;
            });
        var running = server.RunAsync(cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondStarted = false;
            var second = new LocalManagerServer(root, (_, _) => Task.FromResult<object>(new { }),
                afterOwnershipAcquired: _ => { secondStarted = true; return Task.CompletedTask; });
            await Assert.ThrowsAsync<IOException>(() => second.RunAsync(cancellation.Token)); Assert.False(secondStarted);
            await cancellation.CancelAsync(); await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<IOException>(() => new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            release.TrySetResult(); await running.WaitAsync(TimeSpan.FromSeconds(5));
            using var regained = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            await cancellation.CancelAsync(); release.TrySetResult(); await running;
            Directory.Delete(root, true);
        }
    }
}
