namespace ArkServerManager.Core;

/// <summary>Caller holds the cluster work scope. Cleanup is confined to validated index-derived paths.</summary>
public static class CoordinatedArchiveRetention
{
    public static async Task ExpireAsync(CatalogDocument catalog, Guid clusterId, string root, long hour,
        CancellationToken ct = default)
    {
        if (hour < 1) throw new ArgumentOutOfRangeException(nameof(hour));
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        var edition = catalog.Clusters.Single(c => c.Id == clusterId).Edition;
        var index = Path.Combine(storage.Backups, "Coordinated", $"hour-{hour:D8}.json");
        foreach (var record in new[] { index, index + ".pending" })
        {
            CheckPath(record);
            var point = await CoordinatedBackupRecords.ReadBoundedAsync<CoordinatedRestorePoint>(record, ct);
            if (point is null) continue;
            if (point.SchemaVersion != 1 || point.ClusterId != clusterId || point.Edition != edition || point.RuntimeHour != hour
                || point.Capture is null || point.Capture.Id == Guid.Empty || point.Capture.CapturedAt == default
                || point.Maps is null || point.Maps.Count == 0 || point.Maps.Any(id => id == Guid.Empty)
                || point.Maps.Distinct().Count() != point.Maps.Count)
                throw new InvalidDataException("Unexpected coordinated retention record.");
            var name = $"hour-{hour:D8}-{point.Capture.Id:N}.zip";
            var paths = catalog.Servers.Where(s => point.Maps.Contains(s.Id))
                .Select(s => Path.Combine(s.Directory, "backups", "Coordinated", name))
                .Append(Path.Combine(storage.Backups, "Coordinated", name)).ToArray();
            // Validate all locations before deleting any. Removed maps have already lost their associated files.
            foreach (var path in paths) CheckPath(path);
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(path)) File.Delete(path);
            }
            ct.ThrowIfCancellationRequested();
            File.Delete(record);
        }
    }

    private static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Coordinated retention must not follow linked storage.");
    }
}
