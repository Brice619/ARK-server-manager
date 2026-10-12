using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class CoordinatedBackupRecordsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-coordinated-records-" + Guid.NewGuid().ToString("N"));
    private readonly Guid id = Guid.NewGuid();
    private CatalogDocument Catalog => new(1, [new(id, "Cluster", ArkEdition.ASE, new())], []);
    [Fact]
    public async Task EmptyQueryDoesNotCreateAnyFolders()
    {
        Assert.Empty((await CoordinatedBackupRecords.ReadAsync(Catalog, id, root)).Points);
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task OnlyPublishedRetainedPointsAppearAndHistoricalRemovedMapIdentityRemains()
    {
        var storage = ManagedClusterStorage.Resolve(Catalog, id, root); var folder = Path.Combine(storage.Backups, "Coordinated");
        var map = Guid.NewGuid();
        await new AtomicJsonStore<CoordinatedBackupSchedule>(Path.Combine(folder, "schedule.json")).SaveAsync(
            new(1, id, ArkEdition.ASE, TimeSpan.FromHours(11).Ticks, null, [], null, null, []));
        foreach (var hour in new[] { 1, 2, 11 })
            await new AtomicJsonStore<CoordinatedRestorePoint>(Path.Combine(folder, $"hour-{hour:D8}.json")).SaveAsync(
                new(1, id, ArkEdition.ASE, hour, new(Guid.NewGuid(), DateTimeOffset.UtcNow), [map]));
        await File.WriteAllTextAsync(Path.Combine(folder, "hour-00000010.json.pending"), "incomplete journal");
        var result = await CoordinatedBackupRecords.ReadAsync(Catalog, id, root);
        Assert.Equal(new long[] { 11, 2 }, result.Points.Select(p => p.RuntimeHour));
        Assert.All(result.Points, point => Assert.Equal(map, Assert.Single(point.Maps)));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidOwnerOrMissingRuntimeCheckpointIsRejected(bool wrongOwner)
    {
        var storage = ManagedClusterStorage.Resolve(Catalog, id, root); var folder = Path.Combine(storage.Backups, "Coordinated");
        if (wrongOwner) await new AtomicJsonStore<CoordinatedBackupSchedule>(Path.Combine(folder, "schedule.json")).SaveAsync(
            new(1, id, ArkEdition.ASE, TimeSpan.FromHours(1).Ticks, null, [], null, null, []));
        await new AtomicJsonStore<CoordinatedRestorePoint>(Path.Combine(folder, "hour-00000001.json")).SaveAsync(
            new(1, wrongOwner ? Guid.NewGuid() : id, ArkEdition.ASE, 1, new(Guid.NewGuid(), DateTimeOffset.UtcNow), [Guid.NewGuid()]));
        await Assert.ThrowsAsync<InvalidDataException>(() => CoordinatedBackupRecords.ReadAsync(Catalog, id, root));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedSelectionRequiresTheRecordedCaptureIdentity(bool mismatch)
    {
        var storage = ManagedClusterStorage.Prepare(Catalog, id, root);
        var folder = Path.Combine(storage.Backups, "Coordinated");
        var capture = new SnapshotCapture(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var point = new CoordinatedRestorePoint(1, id, ArkEdition.ASE, 1, capture, [Guid.NewGuid()]);
        await new AtomicJsonStore<CoordinatedBackupSchedule>(Path.Combine(folder, "schedule.json")).SaveAsync(
            new(1, id, ArkEdition.ASE, TimeSpan.FromHours(1).Ticks, null, [], null, null, [point]));
        await new AtomicJsonStore<CoordinatedRestorePoint>(Path.Combine(folder, "hour-00000001.json")).SaveAsync(point);
        await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "survivor"), "player data");
        var archive = Path.Combine(folder, $"hour-00000001-{capture.Id:N}.zip");
        await new SnapshotArchive().CreateAsync(storage.Transfers, archive, id, ArkEdition.ASE, 1,
            scope: SnapshotScope.SharedCluster, capture: mismatch ? new(Guid.NewGuid(), capture.CapturedAt) : capture);
        var before = await File.ReadAllBytesAsync(archive);
        if (mismatch) await Assert.ThrowsAsync<InvalidDataException>(() => CoordinatedArchiveSelection.VerifyAsync(Catalog, root, new(id, 1, null)));
        else
        {
            var result = await CoordinatedArchiveSelection.VerifyAsync(Catalog, root, new(id, 1, null));
            Assert.Equal(capture, result.Capture); Assert.Null(result.ServerId); Assert.Equal(1, result.FileCount);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => CoordinatedArchiveSelection.ResolveAsync(Catalog, root, new(id, 1, Guid.NewGuid())));
        await Assert.ThrowsAsync<ArgumentException>(() => CoordinatedArchiveSelection.ResolveAsync(Catalog, root, new(id, 1, point.Maps[0])));
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
