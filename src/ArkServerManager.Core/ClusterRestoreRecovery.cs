namespace ArkServerManager.Core;

/// <summary>Caller holds the verified stopped-cluster scope. Recovery preflights the entire group before moves.</summary>
public static class ClusterRestoreRecovery
{
    public static async Task<ClusterRestoreRecord> RollbackAsync(CatalogDocument catalog, Guid clusterId, string root,
        Guid operationId, CancellationToken ct = default)
    {
        var record = (await ClusterRestoreRecords.ReadAsync(catalog, clusterId, root, ct)).Single(r => r.OperationId == operationId);
        if (record.Phase is not (ClusterRestorePhase.Replacing or ClusterRestorePhase.RollingBack or ClusterRestorePhase.RolledBack))
            throw new InvalidOperationException("Choose an interrupted cluster replacement.");
        var allMembers=catalog.Servers.Where(s=>s.ClusterId==clusterId).OrderBy(s=>s.Id).ToArray();
        var members=allMembers.Where(s=>record.Maps.Contains(s.Id)).ToArray();
        if (!record.Maps.OrderBy(id => id).SequenceEqual(members.Select(s => s.Id)) || !record.Scoped && members.Length!=allMembers.Length)
            throw new InvalidOperationException("Cluster membership changed. Review recovery before moving files.");
        var expected = members.SelectMany(s => new[] { (Server: (Guid?)s.Id, Folder: "SavedArks"), (Server: (Guid?)s.Id, Folder: "SaveGames") })
            .Concat(record.IncludesShared?new[]{(Server:(Guid?)null,Folder:"Transfers")}:[]).ToArray();
        if (record.OriginalFolders is null || record.OriginalFolders.Count != expected.Length
            || record.OriginalFolders.Any(f => f is null) || record.OriginalFolders.Select(f => (f.ServerId, f.Folder)).Distinct().Count() != expected.Length
            || expected.Any(item => !record.OriginalFolders.Any(f => f.ServerId == item.Server && f.Folder == item.Folder)))
            throw new InvalidDataException("The original cluster folder inventory is incomplete.");
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        var items = record.OriginalFolders.Select(original =>
        {
            var directory = original.ServerId is Guid id ? members.Single(s => s.Id == id).Directory : storage.Directory;
            var stageRoot = Path.Combine(directory, ".cluster-restore-stage-" + operationId.ToString("N"));
            var previousRoot = Path.Combine(directory, ".cluster-restore-previous-" + operationId.ToString("N"));
            return (Original: original, Current: original.ServerId is null ? storage.Transfers
                : Path.Combine(directory, "ShooterGame", "Saved", original.Folder),
                Stage: original.ServerId is null ? stageRoot : Path.Combine(stageRoot, original.Folder),
                Previous: Path.Combine(previousRoot, original.Folder), StageRoot: stageRoot, PreviousRoot: previousRoot);
        }).ToArray();
        foreach (var item in items)
        {
            foreach (var path in new[] { item.Current, item.StageRoot, item.PreviousRoot })
            {
                CheckTree(path);
                if (File.Exists(path)) throw new IOException("A cluster recovery root is occupied by a file.");
            }
            if (File.Exists(item.Current) || File.Exists(item.Stage) || File.Exists(item.Previous))
                throw new IOException("A cluster recovery folder is occupied by a file.");
            if (record.Phase == ClusterRestorePhase.RolledBack) continue;
            var current = Directory.Exists(item.Current); var stage = Directory.Exists(item.Stage); var previous = Directory.Exists(item.Previous);
            if (item.Original.HadPrevious ? !previous && !current || previous && current && stage : previous || current && stage)
                throw new InvalidDataException("Cluster recovery locations are inconsistent. No recovery moves began.");
        }
        var store = new AtomicJsonStore<ClusterRestoreRecord>(Path.Combine(storage.Backups, "cluster-restore-transactions", operationId.ToString("N") + ".json"));
        if (record.Phase != ClusterRestorePhase.RolledBack)
        {
            record = record with { Phase = ClusterRestorePhase.RollingBack }; await store.SaveAsync(record, ct);
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(item.Previous))
                {
                    if (Directory.Exists(item.Current))
                    { Directory.CreateDirectory(Path.GetDirectoryName(item.Stage)!); Directory.Move(item.Current, item.Stage); }
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Current)!); Directory.Move(item.Previous, item.Current);
                }
                else if (!item.Original.HadPrevious && Directory.Exists(item.Current))
                { Directory.CreateDirectory(Path.GetDirectoryName(item.Stage)!); Directory.Move(item.Current, item.Stage); }
            }
            record = record with { Phase = ClusterRestorePhase.RolledBack }; await store.SaveAsync(record, CancellationToken.None);
        }
        foreach (var previous in items.Select(i => i.PreviousRoot).Distinct())
            if (Directory.Exists(previous) && Directory.EnumerateFileSystemEntries(previous).Any())
                throw new InvalidDataException("Previous cluster state remains after rollback. Cleanup requires review.");
        foreach (var stage in items.Select(i => i.StageRoot).Distinct()) if (Directory.Exists(stage)) Directory.Delete(stage, true);
        foreach (var previous in items.Select(i => i.PreviousRoot).Distinct()) if (Directory.Exists(previous)) Directory.Delete(previous);
        return record;
    }
    private static void CheckTree(string path)
    {
        for (var parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (Path.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cluster recovery must not follow linked storage.");
        if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) CheckTree(child);
    }
}
