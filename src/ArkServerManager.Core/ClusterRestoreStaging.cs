using System.Security.Cryptography;
using System.Text;

namespace ArkServerManager.Core;

public enum ClusterRestorePhase { Preparing, Staged, Replacing, Restored, RollingBack, RolledBack, Cleaned, Started }
public sealed record ClusterRestoreRecord(int SchemaVersion, Guid OperationId, Guid ClusterId, ArkEdition Edition,
    long RuntimeHour, SnapshotCapture Capture, List<Guid> Maps, ClusterRestorePhase Phase)
{
    public bool Scoped { get; init; }
    public bool IncludesShared { get; init; } = true;
    public List<ClusterStageChecksum> StageChecksums { get; init; } = [];
    public List<ClusterOriginalFolder>? OriginalFolders { get; init; }
}
public sealed record ClusterStageChecksum(Guid? ServerId, string Sha256);
public sealed record ClusterOriginalFolder(Guid? ServerId, string Folder, bool HadPrevious);

/// <summary>Caller owns the verified stopped-cluster scope. Every payload is staged before live-state replacement.</summary>
public static class ClusterRestoreStaging
{
    public static async Task<ClusterRestoreRecord> StageAsync(CatalogDocument catalog, string root,
        CoordinatedClusterRestoreRequest request, Guid operationId, CancellationToken ct = default)
    {
        var prepared = await CoordinatedClusterRestoreSelection.PrepareAsync(catalog, root, request, ct);
        return await StagePreparedAsync(catalog,root,prepared,operationId,false,ct);
    }
    public static async Task<ClusterRestoreRecord> StagePreparedAsync(CatalogDocument catalog,string root,
        PreparedClusterRestore prepared,Guid operationId,bool scoped,CancellationToken ct=default)
    {
        if(operationId==Guid.Empty)throw new ArgumentException("Choose a restore operation identity.");
        var request=new CoordinatedClusterRestoreRequest(prepared.Point.ClusterId,prepared.Point.RuntimeHour,prepared.Point.Capture.Id,true);
        await ClusterRestoreRecords.RequireConsistentAsync(catalog,request.ClusterId,root,ct);
        var storage = ManagedClusterStorage.Resolve(catalog, request.ClusterId, root);
        var journal = Path.Combine(storage.Backups, "cluster-restore-transactions", operationId.ToString("N") + ".json");
        var targets = prepared.Maps.Select(map => (Archive: map, Stage: Path.Combine(map.Server!.Directory,
            ".cluster-restore-stage-" + operationId.ToString("N"))))
            .Concat(prepared.Shared is null ? [] : new[]{(Archive: prepared.Shared, Stage: Path.Combine(storage.Directory, ".cluster-restore-stage-" + operationId.ToString("N")))}).ToArray();
        foreach (var path in targets.Select(target => target.Stage).Append(journal))
        {
            CheckPath(path);
            if (Path.Exists(path)) throw new InvalidOperationException("This cluster restore has already been attempted. Review its recorded outcome.");
        }
        var record = new ClusterRestoreRecord(1, operationId, request.ClusterId, prepared.Point.Edition,
            request.RuntimeHour, prepared.Point.Capture, prepared.Point.Maps, ClusterRestorePhase.Preparing){Scoped=scoped,IncludesShared=prepared.Shared is not null};
        var store = new AtomicJsonStore<ClusterRestoreRecord>(journal);
        await store.SaveAsync(record, ct);
        var checksums = new List<ClusterStageChecksum>();
        foreach (var target in targets)
        {
            var server = target.Archive.Server;
            var manifest = await new SnapshotArchive().ExtractToNewDirectoryAsync(target.Archive.Path, target.Stage,
                server?.Id ?? request.ClusterId, prepared.Point.Edition, ct,
                server is null ? SnapshotScope.SharedCluster : SnapshotScope.Server, request.RuntimeHour);
            if (manifest.Capture != prepared.Point.Capture || manifest.Layout != (server is null ? SnapshotLayout.Unspecified : SnapshotLayout.WorldSavedFolders))
                throw new InvalidDataException("An extracted archive does not match the selected cluster capture.");
            checksums.Add(new(server?.Id, await FingerprintAsync(target.Stage, ct)));
        }
        record = record with { Phase = ClusterRestorePhase.Staged, StageChecksums = checksums };
        await store.SaveAsync(record, ct);
        return record;
    }

    public static async Task<ClusterRestoreRecord> ValidateStagedAsync(CatalogDocument catalog, Guid clusterId, string root,
        Guid operationId, CancellationToken ct = default)
    {
        var record = (await ClusterRestoreRecords.ReadAsync(catalog, clusterId, root, ct)).Single(r => r.OperationId == operationId);
        var allMembers = catalog.Servers.Where(s => s.ClusterId == clusterId).OrderBy(s => s.Id).ToArray();
        var members = allMembers.Where(s=>record.Maps.Contains(s.Id)).ToArray();
        var sharedCount=record.IncludesShared?1:0;
        if (record.Phase != ClusterRestorePhase.Staged || !record.Maps.OrderBy(id => id).SequenceEqual(members.Select(s => s.Id)) || !record.Scoped && members.Length!=allMembers.Length
            || record.StageChecksums is null || record.StageChecksums.Count != members.Length + sharedCount
            || record.StageChecksums.Any(c => c is null || c.Sha256 is null || c.Sha256.Length != 64 || !c.Sha256.All(Uri.IsHexDigit))
            || record.StageChecksums.Select(c => c.ServerId).Distinct().Count() != members.Length + sharedCount
            || !record.StageChecksums.Select(c => c.ServerId).OrderBy(id => id).SequenceEqual(members.Select(s => (Guid?)s.Id).Concat(record.IncludesShared?new Guid?[]{null}:[]).OrderBy(id => id)))
            throw new InvalidDataException("The staged cluster inventory is incomplete or its membership changed.");
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        foreach (var checksum in record.StageChecksums)
        {
            var directory = checksum.ServerId is Guid id ? members.Single(s => s.Id == id).Directory : storage.Directory;
            var stage = Path.Combine(directory, ".cluster-restore-stage-" + operationId.ToString("N"));
            if (await FingerprintAsync(stage, ct) != checksum.Sha256)
                throw new InvalidDataException("Staged cluster data changed. Current server data has not been replaced.");
        }
        return record;
    }

    private static async Task<string> FingerprintAsync(string directory, CancellationToken ct)
    {
        CheckTree(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("A staged cluster payload is missing.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(directory, path).Replace('\\', '/'), StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var folder = Directory.Exists(path);
            hash.AppendData(Encoding.UTF8.GetBytes((folder ? "D:" : "F:") + Path.GetRelativePath(directory, path).Replace('\\', '/') + "\0"));
            if (!folder)
            {
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                hash.AppendData(await SHA256.HashDataAsync(input, ct));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cluster restore staging must not use linked storage.");
    }

    /// <summary>Caller owns the stopped-cluster scope. This only abandons staging before any live replacement.</summary>
    public static async Task AbandonAsync(CatalogDocument catalog, Guid clusterId, string root, Guid operationId, CancellationToken ct = default)
    {
        var record = (await ClusterRestoreRecords.ReadAsync(catalog, clusterId, root, ct)).Single(r => r.OperationId == operationId);
        if (record.Phase is not (ClusterRestorePhase.Preparing or ClusterRestorePhase.Staged or ClusterRestorePhase.RolledBack))
            throw new InvalidOperationException("Live replacement has begun. This restore requires group recovery, not staging cleanup.");
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        var members = catalog.Servers.Where(s => record.Maps.Contains(s.Id)).ToArray();
        if (members.Any(s => s.ClusterId != clusterId || s.Edition != record.Edition))
            throw new InvalidOperationException("A staged map changed clusters. Review recovery storage before cleanup.");
        var paths = members.Select(s => Path.Combine(s.Directory, ".cluster-restore-stage-" + operationId.ToString("N")))
            .Append(Path.Combine(storage.Directory, ".cluster-restore-stage-" + operationId.ToString("N"))).ToArray();
        foreach (var path in paths)
        {
            CheckTree(path);
            if (File.Exists(path)) throw new IOException("A cluster staging location is occupied by a file.");
        }
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        var journal = Path.Combine(storage.Backups, "cluster-restore-transactions", operationId.ToString("N") + ".json");
        await new AtomicJsonStore<ClusterRestoreRecord>(journal).SaveAsync(record with { Phase = ClusterRestorePhase.RolledBack }, ct);
    }
    private static void CheckTree(string path)
    {
        CheckPath(path);
        if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) CheckTree(child);
    }
}
