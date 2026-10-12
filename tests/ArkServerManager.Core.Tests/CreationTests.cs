using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class CreationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-create-test-" + Guid.NewGuid().ToString("N"));
    private CatalogRepository Repository => new(Path.Combine(root, "catalog.json"));
    private static ServerDraft Draft(string name) => new(name, "TheIsland", new() { Name = name }, ["123"]);

    [Fact]
    public async Task ClusterGuidePersistsEachNewMemberAndOffersCreateAnotherOrFinish()
    {
        var workflow = new CreationWorkflow(Repository, root, CreationMode.Cluster, ArkEdition.ASE, "New Cluster", (server, _) =>
        { Directory.CreateDirectory(server.Directory); return Task.CompletedTask; });
        var first = await workflow.CreateAsync(Draft("First"));
        Assert.Equal(CreationPhase.ClusterSummary, workflow.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.CreateAsync(Draft("Too early")));
        await workflow.CreateAnotherAsync();
        var second = await workflow.CreateAsync(Draft("Second"));
        await workflow.FinishAsync();
        Assert.Equal(CreationPhase.Finished, workflow.Phase);
        var loaded = await Repository.ReadAsync();
        Assert.Single(loaded.Clusters);
        Assert.Equal(2, loaded.Servers.Count);
        Assert.Equal(first.ClusterId, second.ClusterId);
        Assert.NotEqual(first.Settings.GamePort, second.Settings.GamePort);
        Assert.True(Directory.Exists(first.Directory));
    }

    [Fact]
    public async Task FailedProvisioningDoesNotPublishServerOrCluster()
    {
        var workflow = new CreationWorkflow(Repository, root, CreationMode.Cluster, ArkEdition.ASE, "Cluster",
            (_, _) => throw new IOException("Download failed"));
        await Assert.ThrowsAsync<IOException>(() => workflow.CreateAsync(Draft("Server")));
        Assert.Equal(CreationPhase.Configure, workflow.Phase);
        Assert.Empty((await Repository.ReadAsync()).Servers);
        Assert.Empty((await Repository.ReadAsync()).Clusters);
    }

    [Fact]
    public async Task RepositoryTransactionsPreserveOtherWritersAndRejectInvalidVersion()
    {
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Repository.ChangeAsync(c => c.AddCluster(
            new(Guid.NewGuid(), "Cluster " + i, ArkEdition.ASE, new() { Name = "Cluster " + i })))));
        Assert.Equal(10, (await Repository.ReadAsync()).Clusters.Count);
        var before = await File.ReadAllTextAsync(Path.Combine(root, "catalog.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repository.ChangeAsync(_ => throw new InvalidOperationException("Rejected")));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(root, "catalog.json")));
        await File.WriteAllTextAsync(Path.Combine(root, "catalog.json"), "{\"SchemaVersion\":99,\"Clusters\":[],\"Servers\":[]}");
        await Assert.ThrowsAsync<InvalidDataException>(() => Repository.ReadAsync());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
