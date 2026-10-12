namespace ArkServerManager.Core;

public sealed record CoordinatedRestorePoint(int SchemaVersion, Guid ClusterId, ArkEdition Edition,
    long RuntimeHour, SnapshotCapture Capture, List<Guid> Maps);

/// <summary>Caller holds the whole-cluster work scope. Only a fully verified set gets a published index.</summary>
public sealed class CoordinatedArchivePublisher
{
    private readonly SnapshotArchive archive = new();

    public async Task<CoordinatedRestorePoint> PublishAsync(CatalogDocument catalog, Guid clusterId, string root,
        long runtimeHour, Func<CancellationToken, Task<CoordinatedClusterBackupState>> acquire, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        if (runtimeHour < 1) throw new ArgumentOutOfRangeException(nameof(runtimeHour));
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        var cluster = catalog.Clusters.Single(c => c.Id == clusterId);
        var maps = catalog.Servers.Where(s => s.ClusterId == clusterId).OrderBy(s => s.Id).ToArray();
        if (maps.Length == 0) throw new InvalidOperationException("The cluster has no maps to publish.");
        var pointPath = Path.Combine(storage.Backups, "Coordinated", $"hour-{runtimeHour:D8}.json");
        var journalPath = pointPath + ".pending";
        CheckPath(pointPath); CheckPath(journalPath);
        var published = await CoordinatedBackupRecords.ReadBoundedAsync<CoordinatedRestorePoint>(pointPath, ct);
        if (published is not null)
        {
            Validate(published); await VerifyAsync(published, ct); return published;
        }
        var pending = await CoordinatedBackupRecords.ReadBoundedAsync<CoordinatedRestorePoint>(journalPath, ct);
        if (pending is not null)
        {
            Validate(pending);
            var files = ArchivePaths(pending).ToArray();
            foreach (var file in files) CheckPath(file.Path);
            if (pending.Maps.SequenceEqual(maps.Select(s => s.Id)) && files.All(file => File.Exists(file.Path)))
            {
                await VerifyAsync(pending, ct);
                ct.ThrowIfCancellationRequested();
                File.Move(journalPath, pointPath, overwrite: false);
                return pending;
            }
            // An incomplete captured set cannot be mixed with a later capture. Remove only journal-owned paths.
            foreach (var file in files)
                if (File.Exists(file.Path)) File.Delete(file.Path);
            File.Delete(journalPath);
        }
        await using var captured = await acquire(ct);
        var point = new CoordinatedRestorePoint(1, clusterId, cluster.Edition, runtimeHour, captured.Capture,
            maps.Select(s => s.Id).ToList());
        Validate(point);
        if (captured.ClusterId != clusterId || !captured.Catalog.Servers.Where(s => s.ClusterId == clusterId)
            .OrderBy(s => s.Id).Select(s => (s.Id, s.Edition, s.Map, s.Directory, s.ClusterId))
            .SequenceEqual(maps.Select(s => (s.Id, s.Edition, s.Map, s.Directory, s.ClusterId))))
            throw new InvalidDataException("Cluster membership changed before archive publication.");
        await new AtomicJsonStore<CoordinatedRestorePoint>(journalPath).SaveAsync(point, ct);
        foreach (var file in ArchivePaths(point))
        {
            CheckPath(file.Path);
            await using var view = file.Scope == SnapshotScope.Server ? captured.GetServerState(file.Owner) : captured.GetSharedState();
            await archive.CreateAsync(view.Directory, file.Path, file.Owner, cluster.Edition, runtimeHour, ct,
                file.Scope, file.Layout, point.Capture);
        }
        await VerifyAsync(point, ct);
        ct.ThrowIfCancellationRequested();
        File.Move(journalPath, pointPath, overwrite: false);
        return point;

        void Validate(CoordinatedRestorePoint point)
        {
            if (point.SchemaVersion != 1 || point.ClusterId != clusterId || point.Edition != cluster.Edition
                || point.RuntimeHour != runtimeHour || point.Capture is null || point.Capture.Id == Guid.Empty
                || point.Capture.CapturedAt == default || point.Maps is null || point.Maps.Count == 0
                || point.Maps.Any(id => id == Guid.Empty) || point.Maps.Distinct().Count() != point.Maps.Count
                || !point.Maps.SequenceEqual(point.Maps.OrderBy(id => id)))
                throw new InvalidDataException("Unexpected coordinated backup journal or restore point.");
        }
        IEnumerable<(string Path, Guid Owner, SnapshotScope Scope, SnapshotLayout Layout)> ArchivePaths(CoordinatedRestorePoint point)
        {
            var fileName = $"hour-{runtimeHour:D8}-{point.Capture.Id:N}.zip";
            foreach (var map in catalog.Servers.Where(s => point.Maps.Contains(s.Id)).OrderBy(s => s.Id))
                yield return (Path.Combine(map.Directory, "backups", "Coordinated", fileName), map.Id,
                    SnapshotScope.Server, SnapshotLayout.WorldSavedFolders);
            yield return (Path.Combine(storage.Backups, "Coordinated", fileName), clusterId,
                SnapshotScope.SharedCluster, SnapshotLayout.Unspecified);
        }
        async Task VerifyAsync(CoordinatedRestorePoint point, CancellationToken token)
        {
            foreach (var file in ArchivePaths(point))
            {
                CheckPath(file.Path);
                var manifest = await archive.VerifyAsync(file.Path, token);
                if (manifest.ServerId != file.Owner || manifest.Edition != cluster.Edition || manifest.RuntimeHour != runtimeHour
                    || manifest.Scope != file.Scope || manifest.Layout != file.Layout || manifest.Capture != point.Capture)
                    throw new InvalidDataException("A coordinated archive does not belong to this captured set.");
            }
        }
    }

    private static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Coordinated backup storage must not contain links.");
    }
}
