using System.Text.Json;
using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class ServerRepairTests
{
    private sealed class Runtime : IServerRuntime
    {
        public ServerState State { get; private set; }
        public int Starts;
        public Task StartAsync(ServerDefinition server, CancellationToken ct) { Starts++; State = ServerState.Running; return Task.CompletedTask; }
        public Task<bool> StopAsync(CancellationToken ct) { State = ServerState.Stopped; return Task.FromResult(true); }
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task RepairRequiresStoppedProofAndDoesNotStopSibling(bool verified, bool restart)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-repair-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json")); var cluster = Guid.NewGuid();
            var first = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root,
                new() { Name = "First", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }, cluster);
            var peer = first with { Id = Guid.NewGuid(), Directory = Path.Combine(root, "peer"), Settings = first.Settings with { Name = "Peer", GamePort = 7787, QueryPort = 27025, RconPort = 32340 } };
            await repository.ChangeAsync(catalog => { catalog.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, first.Settings)); catalog.AddServer(first); catalog.AddServer(peer); });
            var runtimes = new Dictionary<Guid, Runtime>();
            var lifecycle = new ServerLifecycleCoordinator(repository, id => runtimes[id] = new(), (_, _) => Task.CompletedTask);
            await lifecycle.ExecuteAsync(first.Id, LifecycleAction.Start);
            await using var jobs = new BackgroundOperations(8, 1); var repairs = 0;
            var manager = new ServerRepairManagerOperations(repository, jobs, lifecycle,
                (server, _) => Task.FromResult(new InstallationStopCheck(server.Directory, verified ? InstallationStopState.Stopped : InstallationStopState.Unverified, [], [], DateTimeOffset.UtcNow)),
                async (server, _, token) =>
                {
                    Assert.Equal(first.Id, server.Id); Assert.Equal(ServerState.Stopped, runtimes[first.Id].State); Assert.Equal(ServerState.Running, runtimes[peer.Id].State);
                    var checkpoint = await new AtomicJsonStore<ServerRepairRecord>(Path.Combine(root, "app-settings", "repair-" + server.Id.ToString("N") + ".json")).LoadAsync(token);
                    Assert.Equal(ServerRepairPhase.Validating, checkpoint!.Phase);
                    repairs++; return true;
                }, managementRoot: root);
            var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "server-files-repair-start",
                JsonSerializer.SerializeToElement(new ServerRepairSelection(first.Id, root, restart, true)));
            manager.Start(request); manager.Start(request);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (jobs.Find(request.Id)!.FinishedAt is null) await Task.Delay(10, deadline.Token);
            Assert.Equal(verified ? BackgroundOperationState.Succeeded : BackgroundOperationState.Failed, jobs.Find(request.Id)!.State);
            Assert.Equal(verified ? 1 : 0, repairs);
            Assert.Equal(1, runtimes[peer.Id].Starts); Assert.Equal(ServerState.Running, runtimes[peer.Id].State);
            Assert.Equal(verified && restart ? 2 : 1, runtimes[first.Id].Starts);
            await using var freshJobs = new BackgroundOperations(8, 1);
            var freshManager = new ServerRepairManagerOperations(repository, freshJobs, lifecycle,
                (_, _) => throw new InvalidOperationException("Read-only recovery must not inspect or stop processes."),
                (_, _, _) => throw new InvalidOperationException("Read-only recovery must not repeat repair."), managementRoot: root);
            Assert.Null(freshJobs.Find(request.Id));
            var status = await freshManager.ReadAsync(first.Id);
            Assert.Equal(request.Id, status.Record!.OperationId);
            Assert.Equal(verified ? ServerRepairPhase.Completed : ServerRepairPhase.Stopping, status.Record.Phase);
            Assert.Equal(restart, status.Record.StartAfterRepair);
        }
        finally { Directory.Delete(root, true); }
    }
}
