using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class CatalogRemovalTests
{
    [Fact]
    public void RemovingOneMapRetainsOtherRegistrationsAndClusterSettings()
    {
        var catalog = new ServerCatalog();
        var cluster = Guid.NewGuid();
        var settings = new ServerSettings { Name = "Shared", GamePort = 7777, QueryPort = 27015, RconPort = 32330 };
        catalog.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, settings));
        var first = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", "one", settings, cluster);
        var second = first with { Id = Guid.NewGuid(), Directory = "two", Settings = settings with
            { GamePort = 7787, QueryPort = 27025, RconPort = 32340 } };
        catalog.AddServer(first); catalog.AddServer(second);
        catalog.RemoveServer(first.Id);
        Assert.Equal(second.Id, Assert.Single(catalog.Servers).Id);
        Assert.Equal(cluster, Assert.Single(catalog.Clusters).Id);
        Assert.Equal("Shared", catalog.Clusters[0].Settings.Name);
        Assert.Equal(new[] { second.Id }, catalog.OperationTargets(second.Id));
        Assert.Throws<KeyNotFoundException>(() => catalog.RemoveServer(first.Id));
        Assert.Single(catalog.Servers);
    }
}
