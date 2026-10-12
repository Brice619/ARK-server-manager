using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerDeletionTransactionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-delete-transaction-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactNameAndScopeRequiredWhileSharedFilesRemain(bool correctName)
    {
        var serverPath = Path.Combine(root, "map-one");
        var shared = Path.Combine(root, "shared-data");
        Directory.CreateDirectory(serverPath); Directory.CreateDirectory(shared);
        await File.WriteAllTextAsync(Path.Combine(serverPath, "world.ark"), "server state");
        await File.WriteAllTextAsync(Path.Combine(shared, "player.arkprofile"), "shared state");
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var cluster = Guid.NewGuid(); var server = Guid.NewGuid(); var operation = Guid.NewGuid();
        var settings = new ServerSettings { Name = "Selected map", GamePort = 7777, QueryPort = 27015, RconPort = 32330 };
        await repository.ChangeAsync(c =>
        {
            c.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, settings));
            c.AddServer(new(server, ArkEdition.ASE, "TheIsland", serverPath, settings, cluster));
        });
        var authorized = false;
        Task Execute() => ServerDeletionTransaction.ExecuteAsync(repository, operation, server,
            correctName ? "Selected map" : "selected map", root, [shared], (_, change, ct) =>
            { authorized = true; return change(ct); });
        if (correctName)
        {
            await Execute();
            Assert.True(authorized);
            Assert.False(Directory.Exists(serverPath));
            var catalog = await repository.ReadAsync();
            Assert.Empty(catalog.Servers); Assert.Single(catalog.Clusters);
            var record = await new AtomicJsonStore<ServerDeletionRecord>(Path.Combine(root, "app-settings", "deletions", operation.ToString("N") + ".json")).LoadAsync();
            Assert.Equal(ServerDeletionPhase.FilesRemoved, record!.Phase);
            Assert.False(Directory.Exists(record.StagedDirectory));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(Execute);
            Assert.False(authorized);
            Assert.True(Directory.Exists(serverPath));
            Assert.Single((await repository.ReadAsync()).Servers);
        }
        Assert.Equal("shared state", await File.ReadAllTextAsync(Path.Combine(shared, "player.arkprofile")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
