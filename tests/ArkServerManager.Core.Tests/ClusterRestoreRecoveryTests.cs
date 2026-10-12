using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ClusterRestoreRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-cluster-rollback-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialGroupMovesRecoverPreviousStateAndCleanupReplayPreservesNewData(bool sharedInstalled)
    {
        var cluster = Guid.NewGuid(); var operation = Guid.NewGuid();
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "map"), new(), cluster);
        var catalog = new CatalogDocument(1, [new(cluster, "Cluster", ArkEdition.ASE, new())], [server]);
        var storage = ManagedClusterStorage.Prepare(catalog, cluster, root);
        var saved = Path.Combine(server.Directory, "ShooterGame", "Saved");
        var previous = Path.Combine(server.Directory, ".cluster-restore-previous-" + operation.ToString("N"));
        Directory.CreateDirectory(Path.Combine(previous, "SavedArks"));
        await File.WriteAllTextAsync(Path.Combine(previous, "SavedArks", "world.ark"), "previous world");
        Directory.CreateDirectory(Path.Combine(saved, "SavedArks")); await File.WriteAllTextAsync(Path.Combine(saved, "SavedArks", "world.ark"), "restored world");
        Directory.CreateDirectory(Path.Combine(saved, "SaveGames")); await File.WriteAllTextAsync(Path.Combine(saved, "SaveGames", "player"), "restored player");
        Directory.CreateDirectory(Path.Combine(saved, "Config")); await File.WriteAllTextAsync(Path.Combine(saved, "Config", "fixture.ini"), "settings");
        await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player"), "previous shared");
        var sharedPrevious = Path.Combine(storage.Directory, ".cluster-restore-previous-" + operation.ToString("N")); Directory.CreateDirectory(sharedPrevious);
        Directory.Move(storage.Transfers, Path.Combine(sharedPrevious, "Transfers"));
        if (sharedInstalled) { Directory.CreateDirectory(storage.Transfers); await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player"), "restored shared"); }
        var record = new ClusterRestoreRecord(1, operation, cluster, ArkEdition.ASE, 1, new(Guid.NewGuid(), DateTimeOffset.UtcNow), [server.Id], ClusterRestorePhase.Replacing)
            { OriginalFolders = [new(server.Id, "SavedArks", true), new(server.Id, "SaveGames", false), new(null, "Transfers", true)] };
        await new AtomicJsonStore<ClusterRestoreRecord>(Path.Combine(storage.Backups, "cluster-restore-transactions", operation.ToString("N") + ".json")).SaveAsync(record);
        Assert.Equal(ClusterRestorePhase.RolledBack, (await ClusterRestoreRecovery.RollbackAsync(catalog, cluster, root, operation)).Phase);
        Assert.Equal("previous world", await File.ReadAllTextAsync(Path.Combine(saved, "SavedArks", "world.ark")));
        Assert.False(Directory.Exists(Path.Combine(saved, "SaveGames"))); Assert.False(Directory.Exists(previous));
        Assert.Equal("previous shared", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player")));
        Assert.Equal("settings", await File.ReadAllTextAsync(Path.Combine(saved, "Config", "fixture.ini")));
        await File.WriteAllTextAsync(Path.Combine(saved, "SavedArks", "world.ark"), "new world after recovery");
        await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player"), "new shared after recovery");
        await ClusterRestoreRecovery.RollbackAsync(catalog, cluster, root, operation);
        Assert.Equal("new world after recovery", await File.ReadAllTextAsync(Path.Combine(saved, "SavedArks", "world.ark")));
        Assert.Equal("new shared after recovery", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player")));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
