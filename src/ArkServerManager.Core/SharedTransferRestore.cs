using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record SharedTransferRestoreRecord(Guid OperationId, Guid ClusterId, ArkEdition Edition,
    long RuntimeHour, bool HadTransfers, WorldRestorePhase Phase);

/// <summary>Every mutation requires the caller's verified stopped-cluster scope.</summary>
public static class SharedTransferRestore
{
    public static async Task<SharedTransferRestoreRecord> RestoreAsync(CatalogDocument catalog, Guid clusterId, string root,
        string archivePath, long hour, Guid operationId, SnapshotCapture? capture = null, CancellationToken ct = default)
    {
        if (hour < 1 || operationId == Guid.Empty) throw new ArgumentException("Choose a shared restore point and operation.");
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        await RequireConsistentAsync(catalog, clusterId, root, ct);
        var stage = Path.Combine(storage.Directory, ".shared-restore-stage-" + operationId.ToString("N"));
        var previous = Path.Combine(storage.Directory, ".shared-restore-previous-" + operationId.ToString("N"));
        var journalPath = Journal(storage, operationId);
        RejectTree(storage.Transfers); RejectTree(stage); RejectTree(previous); RejectTree(journalPath);
        if (Path.Exists(stage) || Path.Exists(previous) || Path.Exists(journalPath))
            throw new InvalidOperationException("This shared restore was already attempted. Review its outcome.");
        var manifest = await new SnapshotArchive().ExtractToNewDirectoryAsync(archivePath, stage, clusterId,
            catalog.Clusters.Single(c => c.Id == clusterId).Edition, ct, SnapshotScope.SharedCluster, hour);
        if (manifest.Layout != SnapshotLayout.Unspecified || capture is not null && manifest.Capture != capture)
        { Directory.Delete(stage, true); throw new InvalidDataException("The shared archive does not match the selected capture."); }
        var record = new SharedTransferRestoreRecord(operationId, clusterId, manifest.Edition, hour,
            Directory.Exists(storage.Transfers), WorldRestorePhase.Prepared);
        var store = new AtomicJsonStore<SharedTransferRestoreRecord>(journalPath);
        await store.SaveAsync(record, ct);
        record = record with { Phase = WorldRestorePhase.Replacing };
        await store.SaveAsync(record, ct);
        try
        {
            if (record.HadTransfers) Directory.Move(storage.Transfers, previous);
            Directory.Move(stage, storage.Transfers);
            record = record with { Phase = WorldRestorePhase.Replaced };
            await store.SaveAsync(record, CancellationToken.None);
            return record;
        }
        catch
        {
            await RollbackAsync(catalog, clusterId, root, operationId, CancellationToken.None);
            throw;
        }
    }

    public static async Task<List<SharedTransferRestoreRecord>> ReadAsync(CatalogDocument catalog, Guid clusterId, string root,
        CancellationToken ct = default)
    {
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        var folder = Path.Combine(storage.Backups, "shared-restore-transactions"); RejectTree(folder);
        if (!Directory.Exists(folder)) return [];
        var paths = Directory.EnumerateFiles(folder, "*.json").Take(1001).ToArray();
        if (paths.Length > 1000) throw new InvalidDataException("Too many shared restore records require review.");
        var records = new List<SharedTransferRestoreRecord>();
        foreach (var path in paths)
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 65536) throw new InvalidDataException("Shared restore record exceeds its size limit.");
            var record = await JsonSerializer.DeserializeAsync<SharedTransferRestoreRecord>(input, cancellationToken: ct)
                ?? throw new InvalidDataException("Empty shared restore record.");
            if (record.OperationId == Guid.Empty || record.ClusterId != clusterId || record.Edition != catalog.Clusters.Single(c => c.Id == clusterId).Edition
                || record.RuntimeHour < 1 || !Enum.IsDefined(record.Phase) || Path.GetFileName(path) != record.OperationId.ToString("N") + ".json")
                throw new InvalidDataException("Unexpected shared restore record.");
            records.Add(record);
        }
        return records;
    }
    public static async Task RequireConsistentAsync(CatalogDocument catalog, Guid clusterId, string root, CancellationToken ct = default)
    {
        if ((await ReadAsync(catalog, clusterId, root, ct)).Any(r => r.Phase is WorldRestorePhase.Replacing or WorldRestorePhase.RollingBack))
            throw new InvalidOperationException("Interrupted shared replacement requires recovery before any cluster map starts.");
    }
    public static async Task RollbackAsync(CatalogDocument catalog, Guid clusterId, string root, Guid operationId, CancellationToken ct = default)
    {
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        var record = (await ReadAsync(catalog, clusterId, root, ct)).Single(r => r.OperationId == operationId);
        var stage = Path.Combine(storage.Directory, ".shared-restore-stage-" + operationId.ToString("N"));
        var previous = Path.Combine(storage.Directory, ".shared-restore-previous-" + operationId.ToString("N"));
        if (record.Phase == WorldRestorePhase.RolledBack)
        {
            RejectTree(stage); RejectTree(previous);
            if (Directory.Exists(previous)) throw new InvalidDataException("Completed shared rollback still has previous data requiring review.");
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            return;
        }
        if (record.Phase is not (WorldRestorePhase.Replacing or WorldRestorePhase.RollingBack))
            throw new InvalidOperationException("This shared restore is not an interrupted replacement.");
        RejectTree(stage); RejectTree(previous); RejectTree(storage.Transfers);
        if (Directory.Exists(stage) && Directory.Exists(previous) && Directory.Exists(storage.Transfers)
            || record.HadTransfers && !Directory.Exists(previous) && !Directory.Exists(storage.Transfers)
            || !record.HadTransfers && (Directory.Exists(previous) || Directory.Exists(stage) && Directory.Exists(storage.Transfers)))
            throw new InvalidDataException("Shared recovery locations are inconsistent.");
        var store = new AtomicJsonStore<SharedTransferRestoreRecord>(Journal(storage, operationId));
        await store.SaveAsync(record with { Phase = WorldRestorePhase.RollingBack }, ct);
        if (Directory.Exists(previous))
        {
            if (Directory.Exists(storage.Transfers)) Directory.Move(storage.Transfers, stage);
            Directory.Move(previous, storage.Transfers);
        }
        else if (!record.HadTransfers && Directory.Exists(storage.Transfers))
        {
            if (Directory.Exists(stage)) throw new InvalidDataException("Shared staged state conflicts with rollback.");
            Directory.Move(storage.Transfers, stage);
        }
        await store.SaveAsync(record with { Phase = WorldRestorePhase.RolledBack }, CancellationToken.None);
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
    }
    /// <summary>Caller holds the lifecycle gate; readiness must come from owned verified runtime observations.</summary>
    public static async Task CleanupAfterClusterStartAsync(CatalogDocument catalog, Guid clusterId, string root,
        Func<Guid, IReadOnlyList<Guid>, bool> allRunning, CancellationToken ct = default)
    {
        var members = catalog.Servers.Where(s => s.ClusterId == clusterId).Select(s => s.Id).ToArray();
        if (!allRunning(clusterId, members)) return;
        await RequireConsistentAsync(catalog, clusterId, root, ct);
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        foreach (var record in (await ReadAsync(catalog, clusterId, root, ct)).Where(r => r.Phase is WorldRestorePhase.Replaced or WorldRestorePhase.Started))
        {
            var previous = Path.Combine(storage.Directory, ".shared-restore-previous-" + record.OperationId.ToString("N"));
            RejectTree(previous);
            if (File.Exists(previous)) throw new IOException("Previous shared state is occupied by a file.");
            var store = new AtomicJsonStore<SharedTransferRestoreRecord>(Journal(storage, record.OperationId));
            await store.SaveAsync(record with { Phase = WorldRestorePhase.Started }, ct);
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
            await store.SaveAsync(record with { Phase = WorldRestorePhase.Cleaned }, ct);
        }
    }
    private static string Journal(ClusterStorageLocations storage, Guid operation)
        => Path.Combine(storage.Backups, "shared-restore-transactions", operation.ToString("N") + ".json");
    private static void RejectTree(string path)
    {
        for (var parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (Path.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Shared restore paths must not use links.");
        if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) RejectTree(child);
    }
}
