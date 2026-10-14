namespace ArkServerManager.Core;

/// <summary>Caller holds the lifecycle gate. Readiness must be based on owned, verified running observations.</summary>
public static class ClusterRestoreCleanup
{
    public static async Task AfterVerifiedStartAsync(CatalogDocument catalog, Guid clusterId, string root,
        Func<Guid, IReadOnlyList<Guid>, bool> allRunning, CancellationToken ct = default)
    {
        var members = catalog.Servers.Where(s => s.ClusterId == clusterId).OrderBy(s => s.Id).ToArray();
        if (!allRunning(clusterId, members.Select(s => s.Id).ToArray())) return;
        await ClusterRestoreRecords.RequireConsistentAsync(catalog, clusterId, root, ct);
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        foreach (var record in (await ClusterRestoreRecords.ReadAsync(catalog, clusterId, root, ct))
            .Where(r => r.Phase is ClusterRestorePhase.Restored or ClusterRestorePhase.Started))
        {
            if (!record.Maps.OrderBy(id => id).SequenceEqual(members.Where(s=>record.Maps.Contains(s.Id)).Select(s => s.Id)) || !record.Scoped && record.Maps.Count!=members.Length)
                throw new InvalidOperationException("Cluster membership changed after restoration. Previous data needs cleanup review.");
            var paths = members.Where(s=>record.Maps.Contains(s.Id)).Select(s => s.Directory).Concat(record.IncludesShared?new[]{storage.Directory}:[]).SelectMany(directory => new[]
            {
                Path.Combine(directory, ".cluster-restore-previous-" + record.OperationId.ToString("N")),
                Path.Combine(directory, ".cluster-restore-stage-" + record.OperationId.ToString("N"))
            }).ToArray();
            foreach (var path in paths)
            {
                CheckTree(path);
                if (File.Exists(path)) throw new IOException("A previous cluster restore location is occupied by a file.");
            }
            var store = new AtomicJsonStore<ClusterRestoreRecord>(Path.Combine(storage.Backups, "cluster-restore-transactions", record.OperationId.ToString("N") + ".json"));
            await store.SaveAsync(record with { Phase = ClusterRestorePhase.Started }, ct);
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            await store.SaveAsync(record with { Phase = ClusterRestorePhase.Cleaned }, ct);
        }
    }
    private static void CheckTree(string path)
    {
        for (var parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (Path.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cluster restore cleanup must not follow linked storage.");
        if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) CheckTree(child);
    }
}
