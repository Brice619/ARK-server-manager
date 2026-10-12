using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class WorldRestoreManagerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-world-request-" + Guid.NewGuid().ToString("N"));
    private sealed class Runtime : IServerRuntime
    {
        public ServerState State { get; private set; }
        public int Starts, Stops;
        public Task StartAsync(ServerDefinition _, CancellationToken ct) { Starts++; State = ServerState.Running; return Task.CompletedTask; }
        public Task<bool> StopAsync(CancellationToken ct) { Stops++; State = ServerState.Stopped; return Task.FromResult(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoringOneClusterMapLeavesItsSiblingRunning(bool interruptedClusterRestore)
    {
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var cluster = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        ServerSettings Settings(int offset) => new() { Name = "Map", GamePort = 7777 + offset, QueryPort = 27015 + offset, RconPort = 32330 + offset };
        var server = new ServerDefinition(first, ArkEdition.ASE, "TheIsland", Path.Combine(root, "first"), Settings(0), cluster);
        await repository.ChangeAsync(c =>
        {
            c.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, Settings(0))); c.AddServer(server);
            c.AddServer(server with { Id = second, Directory = Path.Combine(root, "second"), Settings = Settings(10) });
        });
        var source = Path.Combine(root, "snapshot"); Directory.CreateDirectory(Path.Combine(source, "SavedArks"));
        await File.WriteAllTextAsync(Path.Combine(source, "SavedArks", "world.ark"), "world");
        var name = "hour-00000001.zip";
        var manifest = await new SnapshotArchive().CreateAsync(source, Path.Combine(server.Directory, "backups", name), first,
            ArkEdition.ASE, 1, layout: SnapshotLayout.WorldSavedFolders);
        await new AtomicJsonStore<AutomaticBackupState>(Path.Combine(server.Directory, "backups", "backup-history.json")).SaveAsync(
            new(1, first, ArkEdition.ASE, TimeSpan.FromHours(1).Ticks, null, null, [new(1, name, manifest.CreatedAt)], null));
        var runtimes = new Dictionary<Guid, Runtime> { [first] = new(), [second] = new() };
        var coordinator = new ServerLifecycleCoordinator(repository, id => runtimes[id], (_, _) => Task.CompletedTask);
        Assert.True((await coordinator.ExecuteAsync(first, LifecycleAction.Start)).Success);
        if (interruptedClusterRestore)
        {
            var operationId = Guid.NewGuid();
            var storage = ManagedClusterStorage.Resolve(await repository.ReadAsync(), cluster, root);
            await new AtomicJsonStore<ClusterRestoreRecord>(Path.Combine(storage.Backups, "cluster-restore-transactions", operationId.ToString("N") + ".json")).SaveAsync(
                new(1, operationId, cluster, ArkEdition.ASE, 1, new(Guid.NewGuid(), DateTimeOffset.UtcNow), [first, second], ClusterRestorePhase.Replacing));
        }
        await using var jobs = new BackgroundOperations(8, 1);
        var manager = new WorldRestoreManagerOperations(repository, jobs, coordinator, root,
            (selected, _) => Task.FromResult(new InstallationStopCheck(Path.GetFullPath(selected.Directory), InstallationStopState.Stopped, [], ["fixture"], DateTimeOffset.UtcNow)));
        var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "server-world-restore-start",
            JsonSerializer.SerializeToElement(new WorldRestoreRequest(first, 1, true, true)));
        manager.Start(request);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (jobs.Find(request.Id)!.FinishedAt is null) await Task.Delay(10, deadline.Token);
        var completed = jobs.Find(request.Id)!;
        if (interruptedClusterRestore)
        {
            Assert.Equal(BackgroundOperationState.Failed, completed.State);
            Assert.False(Directory.Exists(Path.Combine(server.Directory, "ShooterGame", "Saved", "SavedArks")));
            Assert.Equal(0, runtimes[second].Stops);
            Assert.Equal(ServerState.Running, runtimes[second].State);
            return;
        }
        Assert.Equal(BackgroundOperationState.Succeeded, completed.State);
        Assert.True(completed.Result!.Value.Deserialize<WorldRestoreOperationResult>()!.Restore.Restarted);
        Assert.Equal(1, runtimes[first].Stops); Assert.Equal(2, runtimes[first].Starts);
        Assert.Equal(0, runtimes[second].Stops); Assert.Equal(1, runtimes[second].Starts);
        Assert.Equal(ServerState.Running, runtimes[second].State);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}



