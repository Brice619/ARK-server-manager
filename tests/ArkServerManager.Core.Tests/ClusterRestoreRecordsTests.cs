using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ClusterRestoreRecordsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-cluster-restore-records-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData(ClusterRestorePhase.Preparing, false)]
    [InlineData(ClusterRestorePhase.Staged, false)]
    [InlineData(ClusterRestorePhase.Replacing, true)]
    [InlineData(ClusterRestorePhase.RollingBack, true)]
    [InlineData(ClusterRestorePhase.Restored, false)]
    public async Task OnlyInterruptedReplacementOrRecoveryBlocksConsistency(ClusterRestorePhase phase, bool blocked)
    {
        var cluster = Guid.NewGuid(); var operation = Guid.NewGuid();
        var catalog = new CatalogDocument(1, [new(cluster, "Cluster", ArkEdition.ASE, new())], []);
        var storage = ManagedClusterStorage.Resolve(catalog, cluster, root);
        var record = new ClusterRestoreRecord(1, operation, cluster, ArkEdition.ASE, 1,
            new(Guid.NewGuid(), DateTimeOffset.UtcNow), [Guid.NewGuid()], phase);
        await new AtomicJsonStore<ClusterRestoreRecord>(Path.Combine(storage.Backups, "cluster-restore-transactions", operation.ToString("N") + ".json")).SaveAsync(record);
        Assert.Equal(record.OperationId, Assert.Single(await ClusterRestoreRecords.ReadAsync(catalog, cluster, root)).OperationId);
        if (blocked) await Assert.ThrowsAsync<InvalidOperationException>(() => ClusterRestoreRecords.RequireConsistentAsync(catalog, cluster, root));
        else await ClusterRestoreRecords.RequireConsistentAsync(catalog, cluster, root);
    }
    [Theory]
    [InlineData(ClusterRestorePhase.Preparing, true)]
    [InlineData(ClusterRestorePhase.Replacing, false)]
    public async Task StagingAbandonmentPreservesLiveDataAndRefusesReplacementRecovery(ClusterRestorePhase phase, bool allowed)
    {
        var cluster = Guid.NewGuid(); var operation = Guid.NewGuid();
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "map"), new(), cluster);
        var catalog = new CatalogDocument(1, [new(cluster, "Cluster", ArkEdition.ASE, new())], [server]);
        var storage = ManagedClusterStorage.Prepare(catalog, cluster, root);
        var staged = Path.Combine(server.Directory, ".cluster-restore-stage-" + operation.ToString("N")); Directory.CreateDirectory(staged);
        await File.WriteAllTextAsync(Path.Combine(staged, "partial"), "staged");
        var world = Path.Combine(server.Directory, "ShooterGame", "Saved", "SavedArks"); Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "world.ark"), "live world");
        await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player"), "live shared");
        await new AtomicJsonStore<ClusterRestoreRecord>(Path.Combine(storage.Backups, "cluster-restore-transactions", operation.ToString("N") + ".json"))
            .SaveAsync(new(1, operation, cluster, ArkEdition.ASE, 1, new(Guid.NewGuid(), DateTimeOffset.UtcNow), [server.Id], phase));
        if (allowed) { await ClusterRestoreStaging.AbandonAsync(catalog, cluster, root, operation); Assert.False(Directory.Exists(staged)); }
        else { await Assert.ThrowsAsync<InvalidOperationException>(() => ClusterRestoreStaging.AbandonAsync(catalog, cluster, root, operation)); Assert.True(Directory.Exists(staged)); }
        Assert.Equal("live world", await File.ReadAllTextAsync(Path.Combine(world, "world.ark")));
        Assert.Equal("live shared", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player")));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
