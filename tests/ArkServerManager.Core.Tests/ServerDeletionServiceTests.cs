using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerDeletionServiceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task OnlySelectedMemberStopsAndFailedStopPreservesItsFiles(bool stopSucceeds, bool throughManager)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-delete-service-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var cluster = Guid.NewGuid(); var selected = Guid.NewGuid(); var peer = Guid.NewGuid();
            var shared = Path.Combine(root, "shared"); Directory.CreateDirectory(shared);
            await File.WriteAllTextAsync(Path.Combine(shared, "player"), "shared state");
            var map = Path.Combine(root, "map"); var other = Path.Combine(root, "peer");
            Directory.CreateDirectory(map); Directory.CreateDirectory(other);
            await File.WriteAllTextAsync(Path.Combine(map, "world.ark"), "world");
            await repository.ChangeAsync(c =>
            {
                c.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, new()));
                c.AddServer(new(selected, ArkEdition.ASE, "TheIsland", map,
                    new() { Name = "Map", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }, cluster));
                c.AddServer(new(peer, ArkEdition.ASE, "TheIsland", other,
                    new() { Name = "Peer", GamePort = 7779, QueryPort = 27017, RconPort = 32332 }, cluster));
            });
            var runtimes = new Dictionary<Guid, Runtime>();
            var lifecycle = new ServerLifecycleCoordinator(repository, id => runtimes[id] = new Runtime(id != selected || stopSucceeds),
                (_, _) => Task.CompletedTask);
            await lifecycle.ExecuteAsync(selected, LifecycleAction.Start);
            var checks = new List<Guid>();
            var cleanupCalls = 0;
            var service = new ServerDeletionService(repository, lifecycle, (server, _) =>
            {
                checks.Add(server.Id);
                return Task.FromResult(new InstallationStopCheck(server.Directory, InstallationStopState.Stopped, [], [], DateTimeOffset.UtcNow));
            }, (server, _) =>
            {
                Assert.Equal(selected, server.Id);
                Assert.Equal(ServerState.Stopped, runtimes[selected].State);
                Assert.True(File.Exists(Path.Combine(map, "world.ark")));
                cleanupCalls++; return Task.CompletedTask;
            });
            if (throughManager)
            {
                await using var operations = new BackgroundOperations(8, 1);
                var manager = new ServerDeletionManagerOperations(repository, service, operations, root,
                    (_, _) => Task.FromResult<IReadOnlyList<string>>([shared]));
                var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "server-deletion-start",
                    System.Text.Json.JsonSerializer.SerializeToElement(new ServerDeletionSelection(selected, "Map")));
                var first = manager.Start(request); var duplicate = manager.Start(request);
                Assert.Equal(first.Id, duplicate.Id); Assert.Equal(first.CreatedAt, duplicate.CreatedAt);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (operations.Find(request.Id)!.FinishedAt is null) await Task.Delay(5, timeout.Token);
                var result = operations.Find(request.Id)!;
                Assert.Equal(stopSucceeds ? BackgroundOperationState.Succeeded : BackgroundOperationState.Failed, result.State);
                if (stopSucceeds)
                {
                    var record = System.Text.Json.JsonSerializer.Deserialize<ServerDeletionRecord>(result.Result!.Value)!;
                    Assert.Equal(request.Id, record.OperationId); Assert.Equal(ServerDeletionPhase.FilesRemoved, record.Phase);
                }
                Assert.Equal(first.CreatedAt, manager.Start(request).CreatedAt);
            }
            else
            {
                var task = service.DeleteAsync(Guid.NewGuid(), selected, "Map", root, [shared]);
                if (stopSucceeds) Assert.Equal(ServerDeletionPhase.FilesRemoved, (await task).Phase);
                else await Assert.ThrowsAsync<InvalidOperationException>(() => task);
            }
            Assert.Equal(1, runtimes[selected].Stops);
            Assert.Equal(stopSucceeds ? 1 : 0, cleanupCalls);
            Assert.Equal(0, runtimes[peer].Stops);
            Assert.Equal(ServerState.Running, runtimes[peer].State);
            Assert.Equal(!stopSucceeds, Directory.Exists(map));
            Assert.Equal(stopSucceeds ? new[] { selected } : [], checks);
            Assert.Equal("shared state", await File.ReadAllTextAsync(Path.Combine(shared, "player")));
            Assert.Equal(stopSucceeds ? 1 : 2, (await repository.ReadAsync()).Servers.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Runtime(bool stopSucceeds) : IServerRuntime
    {
        public ServerState State { get; private set; } = ServerState.Stopped;
        public int Stops { get; private set; }
        public Task StartAsync(ServerDefinition definition, CancellationToken ct) { State = ServerState.Running; return Task.CompletedTask; }
        public Task<bool> StopAsync(CancellationToken ct) { Stops++; if (stopSucceeds) State = ServerState.Stopped; return Task.FromResult(stopSucceeds); }
    }
}
