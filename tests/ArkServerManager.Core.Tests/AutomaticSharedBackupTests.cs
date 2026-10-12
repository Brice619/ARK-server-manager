using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class AutomaticSharedBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-auto-shared-" + Guid.NewGuid().ToString("N"));
    private sealed class State(string source, string directory, SnapshotCapture? capture = null) : IStableServerState
    {
        public string SourceDirectory => source;
        public string Directory => directory;
        public SnapshotCapture? Capture => capture;
        public bool Disposed;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private static CatalogDocument Catalog(Guid cluster) => new(1, [new(cluster, "Cluster", ArkEdition.ASE, new())], []);

    [Fact]
    public async Task SharedHistoryQueryDoesNotCreateStorage()
    {
        var id = Guid.NewGuid();
        var state = await AutomaticBackupService.ReadRecordedClusterAsync(Catalog(id), id, root);
        Assert.Equal(SnapshotScope.SharedCluster, state.Scope);
        Assert.Empty(state.History); Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task SharedRuntimeBackupsRetainTenPositionsAndKeepCaptureIdentity()
    {
        var id = Guid.NewGuid(); var catalog = Catalog(id);
        var locations = ManagedClusterStorage.Prepare(catalog, id, root);
        await File.WriteAllTextAsync(Path.Combine(locations.Transfers, "survivor"), "shared player state");
        var capture = new SnapshotCapture(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var service = new AutomaticBackupService(catalog, id, root, TimeSpan.FromMinutes(1),
            _ => Task.FromResult<IStableServerState>(new State(locations.Transfers, locations.Transfers, capture)), _ => Task.CompletedTask);
        var session = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        for (var hour = 1; hour <= 11; hour++)
            await service.TickMeasuredAsync(new(session, TimeSpan.FromHours(hour).Ticks), now.AddHours(hour));
        var history = await AutomaticBackupService.ReadRecordedClusterAsync(catalog, id, root);
        Assert.Equal(SnapshotScope.SharedCluster, history.Scope);
        Assert.Equal(10, history.History.Count); Assert.DoesNotContain(history.History, entry => entry.RuntimeHour == 1);
        Assert.False(File.Exists(Path.Combine(locations.Backups, "hour-00000001.zip")));
        var manifest = await new SnapshotArchive().VerifyAsync(Path.Combine(locations.Backups, "hour-00000011.zip"));
        Assert.Equal(id, manifest.ServerId); Assert.Equal(SnapshotScope.SharedCluster, manifest.Scope);
        Assert.Equal(capture, manifest.Capture);
        Assert.Equal("survivor", Assert.Single(manifest.Files).Path);
        await service.TickMeasuredAsync(new(session, TimeSpan.FromHours(11).Ticks), now.AddDays(5));
        Assert.Equal(history.RuntimeTicks, (await service.ReadAsync()).RuntimeTicks);
    }

    [Fact]
    public async Task WrongSharedSourceIsReleasedAndRemainsPendingWithoutPublishing()
    {
        var id = Guid.NewGuid(); var catalog = Catalog(id);
        var locations = ManagedClusterStorage.Prepare(catalog, id, root);
        var wrong = new State(locations.Backups, locations.Backups);
        var service = new AutomaticBackupService(catalog, id, root, TimeSpan.FromMinutes(1),
            _ => Task.FromResult<IStableServerState>(wrong), _ => Task.CompletedTask);
        var failed = await service.TickMeasuredAsync(new(Guid.NewGuid(), TimeSpan.FromHours(1).Ticks), DateTimeOffset.UtcNow);
        Assert.Equal(1, failed.PendingHour); Assert.NotNull(failed.LastError); Assert.True(wrong.Disposed);
        Assert.Empty(Directory.GetFiles(locations.Backups, "*.zip"));
        await new AtomicJsonStore<AutomaticBackupState>(Path.Combine(locations.Backups, "backup-history.json"))
            .SaveAsync(failed with { Scope = SnapshotScope.Server });
        await Assert.ThrowsAsync<InvalidDataException>(() => AutomaticBackupService.ReadRecordedClusterAsync(catalog, id, root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedPublicationIsRecoveredOnlyForMatchingSharedOwner(bool wrongOwner)
    {
        var id = Guid.NewGuid(); var catalog = Catalog(id);
        var locations = ManagedClusterStorage.Prepare(catalog, id, root);
        await File.WriteAllTextAsync(Path.Combine(locations.Transfers, "survivor"), "original shared state");
        var target = Path.Combine(locations.Backups, "hour-00000001.zip");
        var capture = new SnapshotCapture(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await new SnapshotArchive().CreateAsync(locations.Transfers, target, wrongOwner ? Guid.NewGuid() : id,
            ArkEdition.ASE, 1, scope: SnapshotScope.SharedCluster, capture: capture);
        var original = await File.ReadAllBytesAsync(target);
        await File.WriteAllTextAsync(Path.Combine(locations.Transfers, "survivor"), "newer shared state");
        await new AtomicJsonStore<AutomaticBackupState>(Path.Combine(locations.Backups, "backup-history.json")).SaveAsync(
            new(1, id, ArkEdition.ASE, TimeSpan.FromHours(1).Ticks, 1, null, [], "interrupted history publication")
                { Scope = SnapshotScope.SharedCluster });
        var captures = 0;
        var service = new AutomaticBackupService(catalog, id, root, TimeSpan.FromMinutes(1),
            _ => { captures++; throw new InvalidOperationException("Recovery must reuse the published archive"); }, _ => Task.CompletedTask);
        var result = await service.TickAsync(TimeSpan.Zero, false, DateTimeOffset.UtcNow);
        Assert.Equal(0, captures); Assert.Equal(original, await File.ReadAllBytesAsync(target));
        if (wrongOwner)
        {
            Assert.Equal(1, result.PendingHour); Assert.NotNull(result.LastError); Assert.Empty(result.History);
        }
        else
        {
            Assert.Null(result.PendingHour); Assert.Null(result.LastError);
            Assert.Equal(1, Assert.Single(result.History).RuntimeHour);
            Assert.Equal(capture, (await new SnapshotArchive().VerifyAsync(target)).Capture);
        }
        Assert.Equal("newer shared state", await File.ReadAllTextAsync(Path.Combine(locations.Transfers, "survivor")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
