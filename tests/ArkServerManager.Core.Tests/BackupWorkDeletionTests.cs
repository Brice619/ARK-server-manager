using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class BackupWorkDeletionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-backup-delete-" + Guid.NewGuid().ToString("N"));
    private sealed class Runtime : IServerRuntime
    {
        public ServerState State { get; private set; }
        public Task StartAsync(ServerDefinition _, CancellationToken ct) { State = ServerState.Running; return Task.CompletedTask; }
        public Task<bool> StopAsync(CancellationToken ct) { State = ServerState.Stopped; return Task.FromResult(true); }
    }
    private async Task<(ServerDefinition Server, CatalogRepository Repository, ServerLifecycleCoordinator Coordinator)> SetupAsync()
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "server"),
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        Directory.CreateDirectory(server.Directory);
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        await repository.ChangeAsync(c => c.AddServer(server));
        return (server, repository, new(repository, _ => new Runtime(), (_, _) => Task.CompletedTask));
    }
    private static Task<InstallationStopCheck> Stopped(ServerDefinition server, CancellationToken ct)
        => Task.FromResult(new InstallationStopCheck(server.Directory, InstallationStopState.Stopped, [], [], DateTimeOffset.UtcNow));

    [Fact]
    public async Task DeletionWaitsForCheckpointWorkThenRemovesAllFilesAndStaleJobCannotRecreateThem()
    {
        var (server, repository, coordinator) = await SetupAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var backup = coordinator.WithBackupWorkAsync(server, (_, _) => throw new InvalidOperationException("Not due"),
            (_, _) => throw new InvalidOperationException("Not due"), Stopped, async (acquire, ct) =>
            {
                var service = new AutomaticBackupService(server, TimeSpan.FromMinutes(1), acquire, _ => Task.CompletedTask,
                    SnapshotLayout.WorldSavedFolders);
                await service.TickAsync(TimeSpan.FromMinutes(30), true, DateTimeOffset.UtcNow, ct);
                entered.SetResult();
                await release.Task.WaitAsync(ct);
                await service.TickAsync(TimeSpan.FromMinutes(10), true, DateTimeOffset.UtcNow, ct);
            }, deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        Assert.True(File.Exists(Path.Combine(server.Directory, "backups", "backup-history.json")));
        var deletion = coordinator.WithStoppedMemberAsync(server, async ct =>
        {
            await repository.ChangeAsync(c => c.RemoveServer(server.Id), ct);
            Directory.Delete(server.Directory, true);
        }, Stopped, deadline.Token);
        Assert.False(deletion.IsCompleted);
        release.SetResult();
        await backup; await deletion;
        Assert.False(Directory.Exists(server.Directory));
        var executed = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.WithBackupWorkAsync(server,
            (_, _) => Task.CompletedTask, (_, _) => throw new InvalidOperationException(), Stopped,
            (_, _) => { executed = true; return Task.CompletedTask; }, deadline.Token));
        Assert.False(executed); Assert.False(Directory.Exists(server.Directory));
    }

    [Fact]
    public async Task FailedWorkReleasesLifecycleGateAndExpiredCaptureCallbackCannotRunLater()
    {
        var (server, _, coordinator) = await SetupAsync();
        Func<CancellationToken, Task<IStableServerState>>? expired = null;
        await Assert.ThrowsAsync<IOException>(() => coordinator.WithBackupWorkAsync(server,
            (_, _) => Task.CompletedTask, (_, _) => throw new InvalidOperationException(), Stopped,
            (acquire, _) => { expired = acquire; throw new IOException("Checkpoint write failed"); }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => expired!(CancellationToken.None));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.True((await coordinator.ExecuteMemberAsync(server.Id, LifecycleAction.Start, deadline.Token)).Success);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
