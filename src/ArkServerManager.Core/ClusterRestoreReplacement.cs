namespace ArkServerManager.Core;

/// <summary>Caller owns the verified stopped-cluster scope. Interrupted moves retain a group journal and all previous state.</summary>
public static class ClusterRestoreReplacement
{
    public static async Task<ClusterRestoreRecord> ReplaceAsync(CatalogDocument catalog, Guid clusterId, string root,
        Guid operationId, CancellationToken ct = default)
    {
        await ClusterRestoreRecords.RequireConsistentAsync(catalog, clusterId, root, ct);
        var record = await ClusterRestoreStaging.ValidateStagedAsync(catalog, clusterId, root, operationId, ct);
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        var items = new List<(Guid? ServerId, string Folder, string Current, string Stage, string Previous)>();
        foreach (var id in record.Maps)
        {
            var server = catalog.Servers.Single(s => s.Id == id && s.ClusterId == clusterId);
            await WorldRestoreRecovery.RequireConsistentAsync(server, ct);
            foreach (var folder in new[] { "SavedArks", "SaveGames" })
                items.Add((id, folder, Path.Combine(server.Directory, "ShooterGame", "Saved", folder),
                    Path.Combine(server.Directory, ".cluster-restore-stage-" + operationId.ToString("N"), folder),
                    Path.Combine(server.Directory, ".cluster-restore-previous-" + operationId.ToString("N"), folder)));
        }
        await SharedTransferRestore.RequireConsistentAsync(catalog, clusterId, root, ct);
        items.Add((null, "Transfers", storage.Transfers,
            Path.Combine(storage.Directory, ".cluster-restore-stage-" + operationId.ToString("N")),
            Path.Combine(storage.Directory, ".cluster-restore-previous-" + operationId.ToString("N"), "Transfers")));
        foreach (var item in items)
        {
            CheckTree(item.Current); CheckTree(item.Stage); CheckTree(item.Previous);
            if (File.Exists(item.Current) || File.Exists(item.Stage) || Path.Exists(Path.GetDirectoryName(item.Previous)!))
                throw new IOException("A cluster restore location is occupied or previous data already exists. No replacement began.");
        }
        record = record with { OriginalFolders = items.Select(item => new ClusterOriginalFolder(item.ServerId, item.Folder,
            Directory.Exists(item.Current))).ToList() };
        foreach (var item in items) Directory.CreateDirectory(item.Stage);
        var store = new AtomicJsonStore<ClusterRestoreRecord>(Path.Combine(storage.Backups, "cluster-restore-transactions", operationId.ToString("N") + ".json"));
        record = record with { Phase = ClusterRestorePhase.Replacing };
        await store.SaveAsync(record, ct);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(item.Previous)!);
            Directory.CreateDirectory(Path.GetDirectoryName(item.Current)!);
            if (Directory.Exists(item.Current)) Directory.Move(item.Current, item.Previous);
            Directory.Move(item.Stage, item.Current);
        }
        record = record with { Phase = ClusterRestorePhase.Restored };
        await store.SaveAsync(record, CancellationToken.None);
        return record;
    }
    private static void CheckTree(string path)
    {
        for (var parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (Path.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cluster replacement must not follow linked storage.");
        if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) CheckTree(child);
    }
}
