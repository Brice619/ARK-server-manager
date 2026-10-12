using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class DeletedMapBackupRetentionTests
{
    [Fact]
    public async Task DeletionPreservesPeerAndSharedPointsThenRuntimeExpiryRemovesOnlyOldOwnedArchives()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-deleted-map-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json")); var cluster = Guid.NewGuid();
            var servers = Enumerable.Range(0, 2).Select(index => new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "map-" + index),
                new() { Name = "Map " + index, GamePort = 7777 + index * 10, QueryPort = 27015 + index * 10, RconPort = 32330 + index * 10 }, cluster)).ToArray();
            await repository.ChangeAsync(c => { c.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, servers[0].Settings)); foreach (var server in servers) c.AddServer(server); });
            var catalog = await repository.ReadAsync(); var storage = ManagedClusterStorage.Prepare(catalog, cluster, root);
            await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "survivor"), "current shared player state");
            foreach (var server in servers)
            {
                var saved = Path.Combine(server.Directory, "ShooterGame", "Saved", "SavedArks"); Directory.CreateDirectory(saved);
                await File.WriteAllTextAsync(Path.Combine(saved, "world.ark"), "current world " + server.Id);
            }
            var points = new List<CoordinatedRestorePoint>();
            for (long hour = 1; hour <= 2; hour++)
            {
                var capture = new SnapshotCapture(Guid.NewGuid(), DateTimeOffset.UtcNow);
                var point = new CoordinatedRestorePoint(1, cluster, ArkEdition.ASE, hour, capture, servers.Select(s => s.Id).ToList()); points.Add(point);
                var name = $"hour-{hour:D8}-{capture.Id:N}.zip";
                foreach (var server in servers)
                    await new SnapshotArchive().CreateAsync(Path.Combine(server.Directory, "ShooterGame", "Saved"),
                        Path.Combine(server.Directory, "backups", "Coordinated", name), server.Id, ArkEdition.ASE, hour,
                        layout: SnapshotLayout.WorldSavedFolders, capture: capture);
                await new SnapshotArchive().CreateAsync(storage.Transfers, Path.Combine(storage.Backups, "Coordinated", name), cluster, ArkEdition.ASE, hour,
                    scope: SnapshotScope.SharedCluster, capture: capture);
                await new AtomicJsonStore<CoordinatedRestorePoint>(Path.Combine(storage.Backups, "Coordinated", $"hour-{hour:D8}.json")).SaveAsync(point);
            }
            var peerArchives = points.Select(p => Path.Combine(servers[1].Directory, "backups", "Coordinated", $"hour-{p.RuntimeHour:D8}-{p.Capture.Id:N}.zip")).ToArray();
            var sharedArchives = points.Select(p => Path.Combine(storage.Backups, "Coordinated", $"hour-{p.RuntimeHour:D8}-{p.Capture.Id:N}.zip")).ToArray();
            var peerBytes = await File.ReadAllBytesAsync(peerArchives[0]); var sharedBytes = await File.ReadAllBytesAsync(sharedArchives[0]);
            await ServerDeletionTransaction.ExecuteAsync(repository, Guid.NewGuid(), servers[0].Id, servers[0].Settings.Name, root, storage.ProtectedPaths,
                (_, change, ct) => change(ct));
            Assert.False(Directory.Exists(servers[0].Directory));
            Assert.Equal(peerBytes, await File.ReadAllBytesAsync(peerArchives[0])); Assert.Equal(sharedBytes, await File.ReadAllBytesAsync(sharedArchives[0]));
            var after = await repository.ReadAsync();
            var untracked = Path.Combine(servers[1].Directory, "backups", "Coordinated", "untracked.zip"); await File.WriteAllTextAsync(untracked, "unrelated file");
            var checkpoint = Path.Combine(storage.Backups, "Coordinated", "schedule.json");
            var session = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            await new AtomicJsonStore<CoordinatedBackupSchedule>(checkpoint).SaveAsync(new(1, cluster, ArkEdition.ASE,
                TimeSpan.FromHours(10).Ticks, new(session, TimeSpan.FromHours(10).Ticks), [], null, null, points));
            var service = new CoordinatedBackupService(after, cluster, root, (_, _) => throw new IOException("Simulated replacement failure"),
                _ => Task.CompletedTask, (hour, ct) => CoordinatedArchiveRetention.ExpireAsync(after, cluster, root, hour, ct));
            var stopped = await service.TickAsync(new(session, TimeSpan.FromHours(10).Ticks), now.AddDays(3));
            Assert.Equal(2, stopped.History.Count); Assert.All(peerArchives.Concat(sharedArchives), path => Assert.True(File.Exists(path)));
            var expired = await service.TickAsync(new(session, TimeSpan.FromHours(11).Ticks), now.AddDays(3).AddHours(1));
            Assert.Equal(2, Assert.Single(expired.History).RuntimeHour); Assert.Equal(new long[] { 11 }, expired.PendingHours);
            Assert.NotNull(expired.LastError);
            Assert.False(File.Exists(peerArchives[0])); Assert.False(File.Exists(sharedArchives[0]));
            Assert.True(File.Exists(peerArchives[1])); Assert.True(File.Exists(sharedArchives[1]));
            Assert.Equal("unrelated file", await File.ReadAllTextAsync(untracked));
            Assert.Equal("current shared player state", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "survivor")));
            Assert.Equal("current world " + servers[1].Id, await File.ReadAllTextAsync(Path.Combine(servers[1].Directory, "ShooterGame", "Saved", "SavedArks", "world.ark")));
            await CoordinatedArchiveRetention.ExpireAsync(after, cluster, root, 1);
            Assert.True(File.Exists(peerArchives[1]));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
