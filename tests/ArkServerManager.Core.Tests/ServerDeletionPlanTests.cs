using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerDeletionPlanTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-deletion-scope");
    private readonly Guid cluster = Guid.NewGuid();
    private ServerDefinition Server(string path) => new(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", path,
        new() { Name = "Selected map" }, cluster);

    [Fact]
    public void OneMapPlanRetainsClusterSharedDataAndSharedBackups()
    {
        var selected = Server(Path.Combine(root, "map-one"));
        var other = Server(Path.Combine(root, "map-two"));
        var shared = new[] { Path.Combine(root, "shared-data"), Path.Combine(root, "shared-backups") };
        var document = new CatalogDocument(1, [new(cluster, "Cluster", ArkEdition.ASE, new())], [selected, other]);
        var plan = ServerDeletionPlan.Create(document, selected.Id, root, shared);
        Assert.Equal(cluster, plan.PreservedClusterId);
        Assert.Equal(shared, plan.ProtectedSharedPaths);
        Assert.Equal(selected.Directory, plan.ServerDirectory);
        Assert.Equal(2, document.Servers.Count);
        Assert.Single(document.Clusters);
    }

    [Fact]
    public void SharedFilesInsideMapFolderRequireSpecificInventory()
    {
        var selected = Server(Path.Combine(root, "map-one"));
        var document = new CatalogDocument(1, [], [selected]);
        Assert.Throws<InvalidOperationException>(() => ServerDeletionPlan.Create(document, selected.Id, root,
            [Path.Combine(selected.Directory, "cluster-transfers")]));
        Assert.Throws<InvalidOperationException>(() => ServerDeletionPlan.Create(document, selected.Id, root, []));
    }

    [Fact]
    public void OverlappingServersAndManagementRootAreRejected()
    {
        var selected = Server(Path.Combine(root, "map-one"));
        var other = Server(Path.Combine(selected.Directory, "other-map"));
        Assert.Throws<InvalidOperationException>(() => ServerDeletionPlan.Create(new(1, [], [selected, other]), selected.Id, root,
            [Path.Combine(root, "shared")]));
        var broad = selected with { Directory = root };
        Assert.Throws<InvalidOperationException>(() => ServerDeletionPlan.Create(new(1, [], [broad]), broad.Id, root,
            [Path.Combine(root, "shared")]));
    }
}
