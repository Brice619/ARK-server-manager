using System.Collections.Concurrent;
namespace ArkServerManager.Core;

public sealed record ManagedClusterBackupEntry(Guid Id,string Name,bool Scheduled,CoordinatedRestorePoint Point,bool IncludesShared,long Bytes){public bool Legacy{get;init;}};
public sealed record ManagedClusterBackupInventory(Guid ClusterId,List<ManagedClusterBackupEntry> Entries,ManagedBackupPolicy Policy,List<Guid>? Maps=null,bool IncludeShared=true);
public sealed record ManagedClusterBackupCreate(Guid ClusterId,string Name);
public sealed record ManagedClusterBackupSelection(Guid ClusterId,Guid BackupId,bool Confirmed=false);
public sealed record ManagedClusterBackupPreferences(Guid ClusterId,ManagedBackupPolicy Policy,List<Guid> Maps,bool IncludeShared);

public sealed class ManagedClusterBackupStore(CatalogDocument catalog,Guid clusterId,string root)
{
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> Gates=new(StringComparer.OrdinalIgnoreCase);
    public string DirectoryPath=>Path.Combine(ManagedClusterStorage.Resolve(catalog,clusterId,root).Backups,"Managed");
    private string IndexPath=>Path.Combine(DirectoryPath,"index.json");
    private SemaphoreSlim Gate=>Gates.GetOrAdd(IndexPath,_=>new(1,1));
    private AtomicJsonStore<ManagedClusterBackupInventory> Store=>new(IndexPath,true);
    private ClusterDefinition Cluster=>catalog.Clusters.Single(c=>c.Id==clusterId);
    private Guid[] Members=>catalog.Servers.Where(s=>s.ClusterId==clusterId).Select(s=>s.Id).Order().ToArray();
    private async Task<ManagedClusterBackupInventory> LoadAsync(CancellationToken ct)
    {
        ManagedBackupStore.RejectLinks(IndexPath);var value=await Store.LoadAsync(ct)??new(clusterId,[],new(),Members.ToList());
        if(value.ClusterId!=clusterId||value.Entries is null||value.Policy is null||value.Entries.Any(e=>e is null||e.Id==Guid.Empty||string.IsNullOrWhiteSpace(e.Name)||e.Point is null||e.Point.ClusterId!=clusterId||e.Point.Edition!=Cluster.Edition||e.Point.RuntimeHour<1||e.Point.Capture is null||e.Point.Capture.Id==Guid.Empty||e.Point.Capture.CapturedAt==default||e.Point.Maps is null||e.Point.Maps.Count==0||e.Point.Maps.Distinct().Count()!=e.Point.Maps.Count||e.Point.Maps.Contains(Guid.Empty)||e.Bytes<0)||value.Entries.Select(e=>e.Id).Distinct().Count()!=value.Entries.Count)throw new InvalidDataException("Invalid cluster backup inventory.");
        ManagedBackupStore.ValidatePolicy(value.Policy);return value with{Maps=(value.Maps??Members.ToList()).Where(Members.Contains).ToList()};
    }
    public async Task<ManagedClusterBackupInventory> ReadAsync(CancellationToken ct=default){await Gate.WaitAsync(ct);try{var state=await LoadAsync(ct);return state with{Entries=[..state.Entries,..await LegacyEntriesAsync(ct)]};}finally{Gate.Release();}}
    private async Task<List<ManagedClusterBackupEntry>> LegacyEntriesAsync(CancellationToken ct)
    {
        var entries=new List<ManagedClusterBackupEntry>();foreach(var point in (await CoordinatedBackupRecords.ReadAsync(catalog,clusterId,root,ct)).Points)
        {
            var entry=new ManagedClusterBackupEntry(point.Capture.Id,"Automatic cluster backup",true,point,true,0){Legacy=true};long bytes=0;
            foreach(var id in Components(entry)){if(id is {} map&&!catalog.Servers.Any(s=>s.Id==map&&s.ClusterId==clusterId))continue;var path=ArchivePath(entry,id);if(File.Exists(path))bytes+=new FileInfo(path).Length;}
            entries.Add(entry with{Bytes=bytes});
        }return entries;
    }
    public async Task<ManagedClusterBackupInventory> SavePreferencesAsync(ManagedClusterBackupPreferences input,CancellationToken ct=default)
    {
        if(input.ClusterId!=clusterId||input.Maps is null||input.Maps.Count==0||input.Maps.Distinct().Count()!=input.Maps.Count||input.Maps.Any(id=>!Members.Contains(id)))throw new ArgumentException("Include at least one registered cluster map.");ManagedBackupStore.ValidatePolicy(input.Policy);
        await Gate.WaitAsync(ct);try{var state=await LoadAsync(ct);var desired=input.Policy with{Configured=input.Policy.Configured,TimeZoneId=input.Policy.TimeZoneId??TimeZoneInfo.Local.Id,LastCompletedDay=state.Policy.LastCompletedDay};
            if(desired.Enabled&&(!state.Policy.Enabled||state.Policy.Hour!=desired.Hour||state.Policy.Minute!=desired.Minute||state.Policy.TimeZoneId!=desired.TimeZoneId)){var local=TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,TimeZoneInfo.FindSystemTimeZoneById(desired.TimeZoneId));if(local.TimeOfDay>=new TimeSpan(desired.Hour,desired.Minute,0))desired=desired with{LastCompletedDay=local.ToString("yyyy-MM-dd")};}
            state=state with{Policy=desired,Maps=input.Maps.Order().ToList(),IncludeShared=input.IncludeShared};await Store.SaveAsync(state,ct);return state;
        }finally{Gate.Release();}
    }
    public string ArchivePath(ManagedClusterBackupEntry entry,Guid? serverId)
    {
        if(entry.Id==Guid.Empty||serverId is {} id&&!entry.Point.Maps.Contains(id)||serverId is null&&!entry.IncludesShared)throw new ArgumentException("Choose a captured backup component.");
        var path=entry.Legacy?Path.Combine(serverId is {} map?Path.Combine(catalog.Servers.Single(s=>s.Id==map&&s.ClusterId==clusterId).Directory,"backups"):ManagedClusterStorage.Resolve(catalog,clusterId,root).Backups,"Coordinated",$"hour-{entry.Point.RuntimeHour:D8}-{entry.Point.Capture.Id:N}.zip"):Path.Combine(DirectoryPath,entry.Id.ToString("N"),(serverId?.ToString("N")??"shared")+".zip");ManagedBackupStore.RejectLinks(path);return path;
    }
    public async Task<ManagedClusterBackupInventory> CreateAsync(string name,bool scheduled,Func<CancellationToken,Task<CoordinatedClusterBackupState>> acquire,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>120||name.Any(char.IsControl))throw new ArgumentException("Enter a backup name of up to 120 characters.");
        await Gate.WaitAsync(ct);try{var state=await LoadAsync(ct);var maps=state.Maps??Members.ToList();if(maps.Count==0)throw new InvalidOperationException("Include at least one cluster map.");
            await using var captured=await acquire(ct);if(captured.ClusterId!=clusterId||!Members.SequenceEqual(captured.Catalog.Servers.Where(s=>s.ClusterId==clusterId).Select(s=>s.Id).Order()))throw new InvalidDataException("Cluster membership changed during capture.");
            var number=DateTimeOffset.UtcNow.UtcTicks;var entry=new ManagedClusterBackupEntry(Guid.NewGuid(),name.Trim(),scheduled,new(1,clusterId,Cluster.Edition,number,captured.Capture,maps.Order().ToList()),state.IncludeShared,0);
            foreach(var id in maps){await using var view=captured.GetServerState(id);await new SnapshotArchive().CreateAsync(view.Directory,ArchivePath(entry,id),id,Cluster.Edition,number,ct,SnapshotScope.Server,SnapshotLayout.WorldSavedFolders,captured.Capture);}
            if(entry.IncludesShared){await using var shared=captured.GetSharedState();await new SnapshotArchive().CreateAsync(shared.Directory,ArchivePath(entry,null),clusterId,Cluster.Edition,number,ct,SnapshotScope.SharedCluster,SnapshotLayout.Unspecified,captured.Capture);}
            await VerifyEntryAsync(entry,ct);entry=entry with{Bytes=Components(entry).Sum(id=>new FileInfo(ArchivePath(entry,id)).Length)};
            state=state with{Entries=[..state.Entries,entry],Policy=scheduled?state.Policy with{LastCompletedDay=ManagedBackupStore.DueDay(state.Policy,DateTimeOffset.UtcNow)??state.Policy.LastCompletedDay}:state.Policy};await Store.SaveAsync(state,ct);
            if(scheduled)foreach(var old in state.Entries.Where(e=>e.Scheduled).OrderByDescending(e=>e.Point.Capture.CapturedAt).Skip(state.Policy.Keep).ToArray())state=await RemoveAsync(state,old,ct);
            return state;
        }finally{Gate.Release();}
    }
    private static IEnumerable<Guid?> Components(ManagedClusterBackupEntry entry)=>entry.Point.Maps.Select(id=>(Guid?)id).Concat(entry.IncludesShared?new Guid?[]{null}:[]);
    private async Task VerifyEntryAsync(ManagedClusterBackupEntry entry,CancellationToken ct)
    {
        foreach(var id in Components(entry)){var manifest=await new SnapshotArchive().VerifyAsync(ArchivePath(entry,id),ct);if(manifest.ServerId!=(id??clusterId)||manifest.Edition!=Cluster.Edition||manifest.RuntimeHour!=entry.Point.RuntimeHour||manifest.Capture!=entry.Point.Capture||manifest.Scope!=(id is null?SnapshotScope.SharedCluster:SnapshotScope.Server)||manifest.Layout!=(id is null?SnapshotLayout.Unspecified:SnapshotLayout.WorldSavedFolders))throw new InvalidDataException("Cluster archive does not match its recorded capture.");}
    }
    public async Task<ManagedClusterBackupEntry> VerifyAsync(Guid id,CancellationToken ct=default){await Gate.WaitAsync(ct);try{var state=await LoadAsync(ct);var entry=state.Entries.SingleOrDefault(e=>e.Id==id)??(await LegacyEntriesAsync(ct)).Single(e=>e.Id==id);await VerifyEntryAsync(entry,ct);return entry;}finally{Gate.Release();}}
    public async Task<PreparedClusterRestore> PrepareRestoreAsync(Guid id,CancellationToken ct=default)
    {
        var entry=await VerifyAsync(id,ct);var selected=entry.Point.Maps.Select(map=>new SelectedCoordinatedArchive(ArchivePath(entry,map),entry.Point,catalog.Servers.SingleOrDefault(s=>s.Id==map&&s.ClusterId==clusterId&&s.Edition==Cluster.Edition)??throw new InvalidOperationException("A captured map is no longer registered in this cluster."))).ToList();return new(entry.Point,selected,entry.IncludesShared?new(ArchivePath(entry,null),entry.Point,null):null);
    }
    private async Task<ManagedClusterBackupInventory> RemoveAsync(ManagedClusterBackupInventory state,ManagedClusterBackupEntry entry,CancellationToken ct)
    {
        await VerifyEntryAsync(entry,ct);var folder=Path.GetDirectoryName(ArchivePath(entry,entry.Point.Maps[0]))!;var removed=folder+".removed-"+Guid.NewGuid().ToString("N");Directory.Move(folder,removed);var next=state with{Entries=state.Entries.Where(e=>e.Id!=entry.Id).ToList()};try{await Store.SaveAsync(next,ct);}catch{Directory.Move(removed,folder);throw;}
        foreach(var id in Components(entry))File.Delete(Path.Combine(removed,(id?.ToString("N")??"shared")+".zip"));Directory.Delete(removed);return next;
    }
    public async Task<ManagedClusterBackupInventory> DeleteAsync(Guid id,CancellationToken ct=default){await Gate.WaitAsync(ct);try{var state=await LoadAsync(ct);var entry=state.Entries.SingleOrDefault(e=>e.Id==id);if(entry is not null)return await RemoveAsync(state,entry,ct);entry=(await LegacyEntriesAsync(ct)).Single(e=>e.Id==id);await VerifyEntryAsync(entry,ct);await CoordinatedArchiveRetention.ExpireAsync(catalog,clusterId,root,entry.Point.RuntimeHour,ct);return state;}finally{Gate.Release();}}
}
