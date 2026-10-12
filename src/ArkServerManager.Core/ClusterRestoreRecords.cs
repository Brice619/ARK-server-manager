namespace ArkServerManager.Core;

public static class ClusterRestoreRecords
{
    public static async Task<List<ClusterRestoreRecord>> ReadAsync(CatalogDocument catalog, Guid clusterId, string root,
        CancellationToken ct = default)
    {
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        var edition = catalog.Clusters.Single(c => c.Id == clusterId).Edition;
        var folder = Path.Combine(storage.Backups, "cluster-restore-transactions");
        CheckPath(folder);
        var result = new List<ClusterRestoreRecord>();
        if (!Directory.Exists(folder)) return result;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            ct.ThrowIfCancellationRequested(); CheckPath(path);
            if (result.Count >= 1000) throw new InvalidDataException("Too many cluster restore records. Review recovery history.");
            var record = await CoordinatedBackupRecords.ReadBoundedAsync<ClusterRestoreRecord>(path, ct)
                ?? throw new InvalidDataException("A cluster restore record disappeared.");
            if (record.SchemaVersion != 1 || record.OperationId == Guid.Empty || record.ClusterId != clusterId || record.Edition != edition
                || Path.GetFileName(path) != record.OperationId.ToString("N") + ".json" || record.RuntimeHour < 1
                || record.Capture is null || record.Capture.Id == Guid.Empty || record.Capture.CapturedAt == default
                || record.Maps is null || record.Maps.Count == 0 || record.Maps.Any(id => id == Guid.Empty)
                || record.Maps.Distinct().Count() != record.Maps.Count || !Enum.IsDefined(record.Phase))
                throw new InvalidDataException("Unexpected cluster restore record. Review recovery before continuing.");
            result.Add(record);
        }
        return result;
    }
    public static async Task RequireConsistentAsync(CatalogDocument catalog, Guid clusterId, string root, CancellationToken ct = default)
    {
        if ((await ReadAsync(catalog, clusterId, root, ct)).Any(record => record.Phase is ClusterRestorePhase.Replacing or ClusterRestorePhase.RollingBack))
            throw new InvalidOperationException("This cluster has an interrupted restore. Recover the cluster before starting maps or capturing new backups.");
    }
    private static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cluster restore records must not use linked storage.");
    }
}
