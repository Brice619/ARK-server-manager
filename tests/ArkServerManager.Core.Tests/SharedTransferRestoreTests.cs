using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class SharedTransferRestoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-shared-restore-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreValidatesCaptureAndPreservesPreviousTransferState(bool mismatch)
    {
        var id = Guid.NewGuid(); var catalog = new CatalogDocument(1, [new(id, "Cluster", ArkEdition.ASE, new())], []);
        var storage = ManagedClusterStorage.Prepare(catalog, id, root);
        await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player"), "snapshot player");
        var capture = new SnapshotCapture(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var archive = Path.Combine(storage.Backups, "point.zip");
        await new SnapshotArchive().CreateAsync(storage.Transfers, archive, id, ArkEdition.ASE, 1, scope: SnapshotScope.SharedCluster, capture: capture);
        await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player"), "current player");
        var operation = Guid.NewGuid();
        if (mismatch)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => SharedTransferRestore.RestoreAsync(catalog, id, root, archive, 1, operation,
                new(Guid.NewGuid(), capture.CapturedAt)));
            Assert.Equal("current player", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player")));
            Assert.Empty(await SharedTransferRestore.ReadAsync(catalog, id, root));
        }
        else
        {
            var record = await SharedTransferRestore.RestoreAsync(catalog, id, root, archive, 1, operation, capture);
            Assert.Equal(WorldRestorePhase.Replaced, record.Phase);
            Assert.Equal("snapshot player", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player")));
            Assert.Equal("current player", await File.ReadAllTextAsync(Path.Combine(storage.Directory, ".shared-restore-previous-" + operation.ToString("N"), "player")));
        }
    }
    [Fact]
    public async Task InterruptedSharedMoveBlocksStartupAndRollbackRestoresPreviousData()
    {
        var id = Guid.NewGuid(); var catalog = new CatalogDocument(1, [new(id, "Cluster", ArkEdition.ASE, new())], []);
        var storage = ManagedClusterStorage.Prepare(catalog, id, root); var operation = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player"), "original player");
        var stage = Path.Combine(storage.Directory, ".shared-restore-stage-" + operation.ToString("N"));
        var previous = Path.Combine(storage.Directory, ".shared-restore-previous-" + operation.ToString("N"));
        Directory.CreateDirectory(stage); await File.WriteAllTextAsync(Path.Combine(stage, "player"), "restored player");
        Directory.Move(storage.Transfers, previous);
        await new AtomicJsonStore<SharedTransferRestoreRecord>(Path.Combine(storage.Backups, "shared-restore-transactions", operation.ToString("N") + ".json"))
            .SaveAsync(new(operation, id, ArkEdition.ASE, 1, true, WorldRestorePhase.Replacing));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SharedTransferRestore.RequireConsistentAsync(catalog, id, root));
        await SharedTransferRestore.RollbackAsync(catalog, id, root, operation);
        Assert.Equal("original player", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player")));
        Assert.Equal(WorldRestorePhase.RolledBack, Assert.Single(await SharedTransferRestore.ReadAsync(catalog, id, root)).Phase);
        await SharedTransferRestore.RequireConsistentAsync(catalog, id, root);
    }
    [Fact]
    public async Task PreviousSharedStateRemainsUntilAllRegisteredMapsReachRunning()
    {
        var id = Guid.NewGuid();
        var first = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "first"), new(), id);
        var second = first with { Id = Guid.NewGuid(), Directory = Path.Combine(root, "second") };
        var catalog = new CatalogDocument(1, [new(id, "Cluster", ArkEdition.ASE, new())], [first, second]);
        var storage = ManagedClusterStorage.Prepare(catalog, id, root); var operation = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player"), "snapshot");
        var archive = Path.Combine(storage.Backups, "point.zip");
        await new SnapshotArchive().CreateAsync(storage.Transfers, archive, id, ArkEdition.ASE, 1, scope: SnapshotScope.SharedCluster);
        await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player"), "previous");
        await SharedTransferRestore.RestoreAsync(catalog, id, root, archive, 1, operation);
        var previous = Path.Combine(storage.Directory, ".shared-restore-previous-" + operation.ToString("N"));
        var tracker = new ClusterRunningTimeTracker();
        tracker.Observe(first.Id, id, ServerState.Running);
        await SharedTransferRestore.CleanupAfterClusterStartAsync(catalog, id, root, tracker.AllRunning);
        Assert.True(Directory.Exists(previous));
        tracker.Observe(second.Id, id, ServerState.Failed);
        await SharedTransferRestore.CleanupAfterClusterStartAsync(catalog, id, root, tracker.AllRunning);
        Assert.True(Directory.Exists(previous));
        tracker.Observe(second.Id, id, ServerState.Running);
        await SharedTransferRestore.CleanupAfterClusterStartAsync(catalog, id, root, tracker.AllRunning);
        Assert.False(Directory.Exists(previous));
        Assert.Equal(WorldRestorePhase.Cleaned, Assert.Single(await SharedTransferRestore.ReadAsync(catalog, id, root)).Phase);
        Assert.Equal("snapshot", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player")));
    }

    [Fact]
    public async Task CompletedSharedRollbackCleanupPreservesNewTransferData()
    {
        var id = Guid.NewGuid(); var catalog = new CatalogDocument(1, [new(id, "Cluster", ArkEdition.ASE, new())], []);
        var storage = ManagedClusterStorage.Prepare(catalog, id, root); var operation = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player"), "new transfer after rollback");
        var stage = Path.Combine(storage.Directory, ".shared-restore-stage-" + operation.ToString("N"));
        Directory.CreateDirectory(stage); await File.WriteAllTextAsync(Path.Combine(stage, "player"), "discarded restore");
        await new AtomicJsonStore<SharedTransferRestoreRecord>(Path.Combine(storage.Backups, "shared-restore-transactions", operation.ToString("N") + ".json"))
            .SaveAsync(new(operation, id, ArkEdition.ASE, 1, false, WorldRestorePhase.RolledBack));
        await SharedTransferRestore.RollbackAsync(catalog, id, root, operation);
        await SharedTransferRestore.RollbackAsync(catalog, id, root, operation);
        Assert.Equal("new transfer after rollback", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player")));
        Assert.False(Directory.Exists(stage));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
