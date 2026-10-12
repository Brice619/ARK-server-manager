using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ManagedClusterStorageTests
{
    [Fact]
    public void ResolvesWithoutWritingAndPreparationPreservesSharedState()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-cluster-storage-" + Guid.NewGuid().ToString("N"));
        var cluster = Guid.NewGuid();
        var catalog = new CatalogDocument(1, [new(cluster, "Cluster", ArkEdition.ASE, new())], []);
        try
        {
            var expected = ManagedClusterStorage.Resolve(catalog, cluster, root);
            Assert.False(Directory.Exists(root));
            var locations = ManagedClusterStorage.Prepare(catalog, cluster, root);
            Assert.Equal(expected, locations);
            WriteFixture(locations.Transfers, "player.arkprofile", "player data");
            WriteFixture(locations.Backups, "backup.zip", "backup data");
            Assert.Equal(locations, ManagedClusterStorage.Prepare(catalog, cluster, root));
            Assert.Equal("player data", File.ReadAllText(Path.Combine(locations.Transfers, "player.arkprofile")));
            Assert.Equal("backup data", File.ReadAllText(Path.Combine(locations.Backups, "backup.zip")));
            Assert.Equal([locations.Directory], locations.ProtectedPaths);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        static void WriteFixture(string folder, string name, string text) => File.WriteAllText(Path.Combine(folder, name), text);
    }

    [Fact]
    public void RejectsFileBlockerUnknownClusterAndOverlappingServer()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-cluster-storage-" + Guid.NewGuid().ToString("N"));
        var cluster = Guid.NewGuid();
        var catalog = new CatalogDocument(1, [new(cluster, "Cluster", ArkEdition.ASE, new())], []);
        try
        {
            Assert.Throws<ArgumentException>(() => ManagedClusterStorage.Resolve(catalog, Guid.NewGuid(), root));
            Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "Clusters"), "keep");
            Assert.Throws<IOException>(() => ManagedClusterStorage.Prepare(catalog, cluster, root));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "Clusters")));
            catalog.Servers.Add(new(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root, new()));
            Assert.Throws<InvalidOperationException>(() => ManagedClusterStorage.Resolve(catalog, cluster, root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
