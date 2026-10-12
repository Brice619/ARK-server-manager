using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class DeletionRecoveryManagerOperationsTests
{
    [Fact]
    public async Task RecoverySurvivesDesktopDisconnectAndReconnectsToSameJob()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-recovery-reconnect-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = Guid.NewGuid(); var operation = Guid.NewGuid(); var request = Guid.NewGuid();
            var staged = Path.Combine(root, ".ark-delete-" + operation.ToString("N"));
            Directory.CreateDirectory(staged);
            await File.WriteAllTextAsync(Path.Combine(staged, "world.ark"), "state");
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            await new AtomicJsonStore<ServerDeletionRecord>(Path.Combine(root, "app-settings", "deletions", operation.ToString("N") + ".json"))
                .SaveAsync(new(1, operation, id, "Map", Path.Combine(root, "map"), staged, null, [], ServerDeletionPhase.Staged));
            await using var jobs = new BackgroundOperations(8, 1);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scopes = 0;
            var routing = new DeletionRecoveryManagerOperations(repository, root, jobs, async (_, change, ct) =>
            { scopes++; entered.TrySetResult(); await release.Task.WaitAsync(ct); await change(ct); });
            using var hostLifetime = new CancellationTokenSource();
            var host = new LocalManagerServer(root, (message, _) => Task.FromResult<object>(message.Operation == "deletion-recovery-start"
                ? routing.Start(message) : jobs.Find(message.Payload!.Value.Deserialize<OperationStatusRequest>()!.Id)!));
            var running = host.RunAsync(hostLifetime.Token);
            try
            {
                using var desktopLifetime = new CancellationTokenSource();
                var selection = new DeletionRecoverySelection(operation, id, "Map", DeletionRecoveryAction.FinishFileRemoval);
                var first = await new LocalManagerClient(root).CallAsync<BackgroundOperation>("deletion-recovery-start", selection,
                    TimeSpan.FromSeconds(5), desktopLifetime.Token, request);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await desktopLifetime.CancelAsync();
                var reconnect = new LocalManagerClient(root);
                var repeated = await reconnect.CallAsync<BackgroundOperation>("deletion-recovery-start", selection,
                    TimeSpan.FromSeconds(5), requestId: request);
                Assert.Equal(first.CreatedAt, repeated.CreatedAt);
                Assert.Equal(BackgroundOperationState.Running, repeated.State);
                Assert.True(Directory.Exists(staged));
                release.TrySetResult();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                BackgroundOperation status;
                do
                {
                    status = await reconnect.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(request),
                        TimeSpan.FromSeconds(5), deadline.Token);
                    if (!status.FinishedAt.HasValue) await Task.Delay(10, deadline.Token);
                } while (!status.FinishedAt.HasValue);
                Assert.Equal(BackgroundOperationState.Succeeded, status.State);
                Assert.Equal(1, scopes);
                Assert.False(Directory.Exists(staged));
                var result = status.Result!.Value.Deserialize<DeletionRecoveryResult>()!;
                Assert.Equal(operation, result.OperationId);
                Assert.Equal(ServerDeletionPhase.FilesRemoved, result.Phase);
            }
            finally { release.TrySetResult(); await hostLifetime.CancelAsync(); await running; }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmedRecoveryRunsOnceAndRejectsChangedAction(bool correctAction)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-recovery-job-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = Guid.NewGuid(); var operation = Guid.NewGuid();
            var original = Path.Combine(root, "map"); var staged = Path.Combine(root, ".ark-delete-" + operation.ToString("N"));
            Directory.CreateDirectory(staged);
            await File.WriteAllTextAsync(Path.Combine(staged, "world.ark"), "state");
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            await new AtomicJsonStore<ServerDeletionRecord>(Path.Combine(root, "app-settings", "deletions", operation.ToString("N") + ".json"))
                .SaveAsync(new(1, operation, id, "Map", original, staged, null, [], ServerDeletionPhase.Staged));
            await using var jobs = new BackgroundOperations(8, 1);
            var scopes = 0;
            var routing = new DeletionRecoveryManagerOperations(repository, root, jobs,
                (_, change, ct) => { scopes++; return change(ct); });
            var selection = new DeletionRecoverySelection(operation, id, "Map", correctAction
                ? DeletionRecoveryAction.FinishFileRemoval : DeletionRecoveryAction.RestoreStagedFiles);
            var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "deletion-recovery-start",
                JsonSerializer.SerializeToElement(selection));
            var first = Assert.IsType<BackgroundOperation>(routing.Start(request));
            var duplicate = Assert.IsType<BackgroundOperation>(routing.Start(request));
            Assert.Equal(first.Id, duplicate.Id); Assert.Equal(first.CreatedAt, duplicate.CreatedAt);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!jobs.Find(first.Id)!.FinishedAt.HasValue) await Task.Delay(10, deadline.Token);
            Assert.Equal(correctAction ? BackgroundOperationState.Succeeded : BackgroundOperationState.Failed, jobs.Find(first.Id)!.State);
            Assert.Equal(correctAction ? 1 : 0, scopes);
            Assert.Equal(!correctAction, Directory.Exists(staged));
            if (!correctAction) Assert.Equal("state", await File.ReadAllTextAsync(Path.Combine(staged, "world.ark")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
