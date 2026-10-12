using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class SharedClusterSnapshotTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-shared-snapshot-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CopiesOnlyTransfersWhileLeaseIsHeldAndNeverReplacesExistingBackup()
    {
        var id = Guid.NewGuid();
        var catalog = new CatalogDocument(1, [new(id, "Cluster", ArkEdition.ASA, new())], []);
        var locations = ManagedClusterStorage.Prepare(catalog, id, root);
        await File.WriteAllTextAsync(Path.Combine(locations.Transfers, "survivor"), "shared data");
        await File.WriteAllTextAsync(Path.Combine(locations.Backups, "existing"), "keep backup");
        var lease = new Lease(locations.Transfers);
        var service = new SharedClusterSnapshotService();
        var result = await service.CreateAsync(catalog, id, root, 1, (selected, _) =>
        {
            Assert.Equal(locations, selected);
            Assert.False(lease.Disposed);
            return Task.FromResult<IStableServerState>(lease);
        });
        Assert.True(lease.Disposed);
        Assert.Equal(id, result.ServerId);
        Assert.Equal(SnapshotScope.SharedCluster, result.Scope);
        Assert.Equal("survivor", Assert.Single(result.Files).Path);
        var target = Path.Combine(locations.Backups, "hour-00000001.zip");
        var bytes = await File.ReadAllBytesAsync(target);
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(catalog, id, root, 1,
            (_, _) => Task.FromResult<IStableServerState>(new Lease(locations.Transfers))));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        Assert.Equal("keep backup", await File.ReadAllTextAsync(Path.Combine(locations.Backups, "existing")));
    }

    [Fact]
    public async Task WrongOrFailedLeasePublishesNoArchiveAndWrongLeaseIsReleased()
    {
        var id = Guid.NewGuid();
        var catalog = new CatalogDocument(1, [new(id, "Cluster", ArkEdition.ASE, new())], []);
        var locations = ManagedClusterStorage.Prepare(catalog, id, root);
        var service = new SharedClusterSnapshotService();
        var wrong = new Lease(locations.Backups);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(catalog, id, root, 1,
            (_, _) => Task.FromResult<IStableServerState>(wrong)));
        Assert.True(wrong.Disposed);
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(catalog, id, root, 1,
            (_, _) => Task.FromException<IStableServerState>(new IOException("Save coordination failed"))));
        Assert.Empty(Directory.EnumerateFiles(locations.Backups));
    }

    [Fact]
    public async Task ArchivesImmutableViewRatherThanChangingLiveTransferDirectory()
    {
        var id = Guid.NewGuid();
        var catalog = new CatalogDocument(1, [new(id, "Cluster", ArkEdition.ASA, new())], []);
        var locations = ManagedClusterStorage.Prepare(catalog, id, root);
        var captured = Path.Combine(root, "immutable-transfers");
        Directory.CreateDirectory(captured);
        await File.WriteAllTextAsync(Path.Combine(captured, "survivor"), "captured player state");
        await File.WriteAllTextAsync(Path.Combine(locations.Transfers, "survivor"), "later player state");
        var lease = new Lease(captured, locations.Transfers);
        await new SharedClusterSnapshotService().CreateAsync(catalog, id, root, 1,
            (_, _) => Task.FromResult<IStableServerState>(lease));
        var extracted = Path.Combine(root, "restored-transfers");
        await new SnapshotArchive().ExtractToNewDirectoryAsync(Path.Combine(locations.Backups, "hour-00000001.zip"),
            extracted, id, ArkEdition.ASA, expectedScope: SnapshotScope.SharedCluster);
        Assert.Equal("captured player state", await File.ReadAllTextAsync(Path.Combine(extracted, "survivor")));
        Assert.Equal("later player state", await File.ReadAllTextAsync(Path.Combine(locations.Transfers, "survivor")));
        Assert.True(lease.Disposed);
    }

    private sealed class Lease(string directory, string? sourceDirectory = null) : IStableServerState
    {
        public string Directory => directory;
        public string SourceDirectory => sourceDirectory ?? directory;
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
