using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class BackgroundOperationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-jobs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WorkSurvivesClientDisconnectAndSameRequestDoesNotStartTwice()
    {
        await using var operations = new BackgroundOperations(8, 1);
        using var hostLifetime = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var server = new LocalManagerServer(root, (request, _) => Task.FromResult<object>(
            request.Operation == "start" ? operations.Submit(request.Id, "fixture", request.Payload, async (progress, ct) =>
            {
                Interlocked.Increment(ref starts);
                progress(new("Working", 25));
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new { Value = 42 };
            }) : operations.Find(request.Payload!.Value.GetGuid())!), () => operations.DisposeAsync().AsTask());
        var hosting = server.RunAsync(hostLifetime.Token);
        try
        {
            var id = Guid.NewGuid();
            using var clientLifetime = new CancellationTokenSource();
            await new LocalManagerClient(root).CallAsync<BackgroundOperation>("start", null, TimeSpan.FromSeconds(5), clientLifetime.Token, id);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await clientLifetime.CancelAsync();
            var reconnect = new LocalManagerClient(root);
            var duplicate = await reconnect.CallAsync<BackgroundOperation>("start", null, TimeSpan.FromSeconds(5), requestId: id);
            Assert.Equal(id, duplicate.Id);
            Assert.Equal(1, starts);
            var status = await reconnect.CallAsync<BackgroundOperation>("status", id, TimeSpan.FromSeconds(5));
            Assert.Equal(BackgroundOperationState.Running, status.State);
            Assert.Equal(25, status.Progress!.Percent);
            release.TrySetResult();
            var finished = await FinishedAsync(operations, id);
            Assert.Equal(BackgroundOperationState.Succeeded, finished.State);
            Assert.Equal(42, finished.Result!.Value.GetProperty("Value").GetInt32());
        }
        finally { await hostLifetime.CancelAsync(); await hosting; }
    }

    [Fact]
    public async Task CapacityNeverDiscardsUnfinishedWorkAndShutdownCancelsQueuedWork()
    {
        var operations = new BackgroundOperations(2, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = operations.Submit(Guid.NewGuid(), "first", null, async (_, ct) =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new { }; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedStarted = false;
        var queued = operations.Submit(Guid.NewGuid(), "queued", null, (_, _) =>
        { queuedStarted = true; return Task.FromResult<object>(new { }); });
        try
        {
            Assert.Throws<InvalidOperationException>(() => operations.Submit(Guid.NewGuid(), "overflow", null, (_, _) => Task.FromResult<object>(new { })));
            Assert.Throws<ArgumentException>(() => operations.Submit(first.Id, "changed", null, (_, _) => Task.FromResult<object>(new { })));
        }
        finally { await operations.DisposeAsync(); }
        Assert.False(queuedStarted);
        Assert.Equal(BackgroundOperationState.Canceled, operations.Find(first.Id)!.State);
        Assert.Equal(BackgroundOperationState.Canceled, operations.Find(queued.Id)!.State);
    }

    [Fact]
    public async Task FailedWorkIsReportedAndExpiredResultIsUnknown()
    {
        await using var operations = new BackgroundOperations(1, 1);
        var failed = operations.Submit(Guid.NewGuid(), "failed", null, (_, _) => throw new IOException("Fixture failure"));
        var result = await FinishedAsync(operations, failed.Id);
        Assert.Equal(BackgroundOperationState.Failed, result.State);
        Assert.Equal("Fixture failure", result.Error);
        // FinishedAt publication happens just before the worker task returns.
        BackgroundOperation? replacement = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (replacement is null)
        {
            try { replacement = operations.Submit(Guid.NewGuid(), "replacement", null, (_, _) => Task.FromResult<object>(new { })); }
            catch (InvalidOperationException) { await Task.Delay(10, timeout.Token); }
        }
        Assert.Null(operations.Find(failed.Id));
        await FinishedAsync(operations, replacement.Id);
    }

    [Fact]
    public async Task HostKeepsOwnershipUntilBackgroundShutdownFinishes()
    {
        using var lifetime = new CancellationTokenSource();
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new LocalManagerServer(root, (_, _) => Task.FromResult<object>(new { Ready = true }), async () =>
        { draining.SetResult(); await drained.Task; });
        var running = host.RunAsync(lifetime.Token);
        try
        {
            await new LocalManagerClient(root).CallAsync<JsonElement>("ping", null, TimeSpan.FromSeconds(5));
            await lifetime.CancelAsync();
            await draining.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<IOException>(() => new LocalManagerServer(root, (_, _) => Task.FromResult<object>(new { })).RunAsync(default));
        }
        finally { drained.TrySetResult(); await lifetime.CancelAsync(); await running; }
        await new LocalManagerServer(root, (_, _) => Task.FromResult<object>(new { })).RunAsync(lifetime.Token);
    }

    private static async Task<BackgroundOperation> FinishedAsync(BackgroundOperations operations, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var result = operations.Find(id)!;
            if (result.FinishedAt.HasValue) return result;
            await Task.Delay(10, timeout.Token);
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
