using System.Text.Json;
using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class ClusterOverrideManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavesOnlyReviewedMapAndRefusesStaleOverrideSelection(bool stale)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-cluster-overrides-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "catalog.json"); var repository = new CatalogRepository(file); var cluster = Guid.NewGuid();
            var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "map"),
                new() { Name = "Map", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }, cluster);
            var peer = server with { Id = Guid.NewGuid(), Directory = Path.Combine(root, "peer"), Settings = server.Settings with { Name = "Peer", GamePort = 7787, QueryPort = 27025, RconPort = 32340 } };
            await repository.ChangeAsync(catalog => { catalog.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, server.Settings)); catalog.AddServer(server); catalog.AddServer(peer); });
            var lifecycle = new ServerLifecycleCoordinator(repository, _ => throw new InvalidOperationException("Saving override metadata must not start servers."), (_, _) => Task.CompletedTask);
            await using var jobs = new BackgroundOperations(8, 1); var manager = new ClusterOverrideManagerOperations(repository, jobs, lifecycle);
            var expected = await manager.ReadAsync(server.Id, default);
            if (stale) await repository.ChangeAsync(catalog => catalog.SetClusterOverrides(server.Id, [], true, false));
            var edit = new ClusterOverrideSave(expected, [new("Game.ini", "MyMod", "Option")], false, true, true);
            var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "cluster-overrides-save-start", JsonSerializer.SerializeToElement(edit));
            manager.Start(request); manager.Start(request);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (jobs.Find(request.Id)!.FinishedAt is null) await Task.Delay(10, deadline.Token);
            Assert.Equal(stale ? BackgroundOperationState.Failed : BackgroundOperationState.Succeeded, jobs.Find(request.Id)!.State);
            var persisted = await new CatalogRepository(file).ReadAsync(); var map = persisted.Servers.Single(item => item.Id == server.Id);
            Assert.Equal(stale, map.OverrideClusterMaxPlayers); Assert.Equal(!stale, map.OverrideClusterMods);
            Assert.Equal(stale ? 0 : 1, map.ClusterOverrides.Count);
            var sibling = persisted.Servers.Single(item => item.Id == peer.Id); Assert.Empty(sibling.ClusterOverrides); Assert.False(sibling.OverrideClusterMods);
            Assert.Equal(70, map.Settings.MaxPlayers); Assert.Empty(map.Mods);
            map.ClusterOverrides.Clear(); Assert.Equal(stale ? 0 : 1, (await repository.ReadAsync()).Servers.Single(item => item.Id == server.Id).ClusterOverrides.Count);
        }
        finally { Directory.Delete(root, true); }
    }
}
