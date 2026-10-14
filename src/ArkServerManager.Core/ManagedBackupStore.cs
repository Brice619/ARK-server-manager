using System.Collections.Concurrent;

namespace ArkServerManager.Core;

public sealed record ManagedBackupEntry(Guid Id,string Name,string Kind,DateTimeOffset CreatedAt,long Bytes,long ArchiveNumber);
public sealed record ManagedBackupPolicy(bool Configured=false,bool Enabled=false,int Hour=3,int Minute=0,int Keep=7,string? TimeZoneId=null,string? LastCompletedDay=null);
public sealed record ManagedBackupInventory(Guid ServerId,List<ManagedBackupEntry> Entries,ManagedBackupPolicy Policy);
public sealed record ManagedBackupCreate(Guid ServerId,string Name);
public sealed record ManagedBackupSelection(Guid ServerId,Guid BackupId,bool Confirmed=false);
public sealed record ManagedBackupSchedule(Guid ServerId,ManagedBackupPolicy Policy);

/// <summary>User-named world/player archives, separate from existing runtime-hour retention.</summary>
public sealed class ManagedBackupStore(ServerDefinition server)
{
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> Gates=new(StringComparer.OrdinalIgnoreCase);
    public string DirectoryPath=>Path.Combine(server.Directory,"backups","managed");
    private string IndexPath=>Path.Combine(DirectoryPath,"index.json");
    private SemaphoreSlim Gate=>Gates.GetOrAdd(Path.GetFullPath(IndexPath),_=>new(1,1));
    private AtomicJsonStore<ManagedBackupInventory> Store=>new(IndexPath,true);
    public static void RejectLinks(string path)
    {
        var current=Path.GetFullPath(path);while(current is not null){if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("The selected storage path contains a link.");current=Path.GetDirectoryName(current);}
    }
    private async Task<ManagedBackupInventory> LoadAsync(CancellationToken ct)
    {
        RejectLinks(IndexPath);var state=await Store.LoadAsync(ct)??new(server.Id,[],new());
        if(state.ServerId!=server.Id||state.Entries is null||state.Policy is null||state.Entries.Any(e=>e is null||string.IsNullOrWhiteSpace(e.Name)||e.Name.Length>120||e.Name.Any(char.IsControl)||e.Kind is not ("Manual" or "Scheduled")||e.Id==Guid.Empty||e.ArchiveNumber<1||e.Bytes<0||e.CreatedAt==default)||state.Entries.Select(e=>e.Id).Distinct().Count()!=state.Entries.Count)throw new InvalidDataException("Backup inventory is invalid.");ValidatePolicy(state.Policy);return state;
    }
    public async Task<ManagedBackupInventory> ReadAsync(CancellationToken ct=default){await Gate.WaitAsync(ct);try{return await LoadAsync(ct);}finally{Gate.Release();}}
    public static void ValidatePolicy(ManagedBackupPolicy policy)
    {
        if(policy.Hour is <0 or >23||policy.Minute is <0 or >59||policy.Keep is <1 or >1000)throw new ArgumentException("Choose a valid backup time and retention count.");
        if(policy.TimeZoneId is not null)_=TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId);
    }
    public async Task<ManagedBackupInventory> ScheduleAsync(ManagedBackupPolicy policy,CancellationToken ct=default,DateTimeOffset? now=null)
    {
        ValidatePolicy(policy);await Gate.WaitAsync(ct);try
        {
            var state=await LoadAsync(ct);var desired=policy with{Configured=true,TimeZoneId=policy.TimeZoneId??TimeZoneInfo.Local.Id,LastCompletedDay=state.Policy.LastCompletedDay};
            // A newly enabled daily schedule starts at its next occurrence,
            // rather than treating an earlier time today as a missed job.
            if(desired.Enabled&&(!state.Policy.Enabled||state.Policy.Hour!=desired.Hour||state.Policy.Minute!=desired.Minute||state.Policy.TimeZoneId!=desired.TimeZoneId))
            {var local=TimeZoneInfo.ConvertTime(now??DateTimeOffset.UtcNow,TimeZoneInfo.FindSystemTimeZoneById(desired.TimeZoneId));if(local.TimeOfDay>=new TimeSpan(desired.Hour,desired.Minute,0))desired=desired with{LastCompletedDay=local.ToString("yyyy-MM-dd")};}
            state=state with{Policy=desired};await Store.SaveAsync(state,ct);return state;
        }finally{Gate.Release();}
    }
    public static string? DueDay(ManagedBackupPolicy policy,DateTimeOffset now)
    {
        ValidatePolicy(policy);if(!policy.Enabled)return null;var local=TimeZoneInfo.ConvertTime(now,TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId??TimeZoneInfo.Local.Id));var day=local.ToString("yyyy-MM-dd");return local.TimeOfDay>=new TimeSpan(policy.Hour,policy.Minute,0)&&policy.LastCompletedDay!=day?day:null;
    }
    public string ArchivePath(Guid id){if(id==Guid.Empty)throw new ArgumentException("Choose a backup.");var path=Path.Combine(DirectoryPath,"backup-"+id.ToString("N")+".zip");RejectLinks(path);return path;}
    public async Task<ManagedBackupInventory> CreateAsync(string name,bool scheduled,Func<CancellationToken,Task<IStableServerState>> acquire,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>120||name.Any(char.IsControl))throw new ArgumentException("Enter a backup name of up to 120 characters.");
        await Gate.WaitAsync(ct);try
        {
            var state=await LoadAsync(ct);var id=Guid.NewGuid();var number=DateTimeOffset.UtcNow.UtcTicks;var target=ArchivePath(id);
            await using var stable=await acquire(ct);var expected=Path.GetFullPath(Path.Combine(server.Directory,"ShooterGame","Saved"));if(!Path.TrimEndingDirectorySeparator(Path.GetFullPath(stable.SourceDirectory)).Equals(Path.TrimEndingDirectorySeparator(expected),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new InvalidDataException("The backup source changed.");
            var manifest=await new SnapshotArchive().CreateAsync(stable.Directory,target,server.Id,server.Edition,number,ct,layout:SnapshotLayout.WorldSavedFolders,capture:stable.Capture);
            var entry=new ManagedBackupEntry(id,name.Trim(),scheduled?"Scheduled":"Manual",manifest.CreatedAt,new FileInfo(target).Length,number);state=state with{Entries=[..state.Entries,entry]};
            if(scheduled){var day=DueDay(state.Policy,DateTimeOffset.UtcNow);state=state with{Policy=state.Policy with{LastCompletedDay=day??state.Policy.LastCompletedDay}};}
            await Store.SaveAsync(state,ct);
            if(scheduled)foreach(var old in state.Entries.Where(e=>e.Kind=="Scheduled").OrderByDescending(e=>e.CreatedAt).Skip(state.Policy.Keep).ToArray()){await VerifyEntryAsync(old,ct);state=await RemoveEntryAsync(state,old,ct);}
            return state;
        }finally{Gate.Release();}
    }
    private async Task VerifyEntryAsync(ManagedBackupEntry entry,CancellationToken ct){var manifest=await new SnapshotArchive().VerifyAsync(ArchivePath(entry.Id),ct);if(manifest.ServerId!=server.Id||manifest.Edition!=server.Edition||manifest.RuntimeHour!=entry.ArchiveNumber||manifest.Layout!=SnapshotLayout.WorldSavedFolders)throw new InvalidDataException("Backup belongs to another server.");}
    public async Task<ManagedBackupEntry> VerifyAsync(Guid id,CancellationToken ct=default){await Gate.WaitAsync(ct);try{var entry=(await LoadAsync(ct)).Entries.Single(e=>e.Id==id);await VerifyEntryAsync(entry,ct);return entry;}finally{Gate.Release();}}
    public async Task<ManagedBackupInventory> DeleteAsync(Guid id,CancellationToken ct=default){await Gate.WaitAsync(ct);try{var state=await LoadAsync(ct);var entry=state.Entries.Single(e=>e.Id==id);await VerifyEntryAsync(entry,ct);return await RemoveEntryAsync(state,entry,ct);}finally{Gate.Release();}}
    private async Task<ManagedBackupInventory> RemoveEntryAsync(ManagedBackupInventory state,ManagedBackupEntry entry,CancellationToken ct)
    {
        var path=ArchivePath(entry.Id);var removed=path+".removed-"+Guid.NewGuid().ToString("N");File.Move(path,removed);
        var next=state with{Entries=state.Entries.Where(e=>e.Id!=entry.Id).ToList()};
        try{await Store.SaveAsync(next,ct);}catch{File.Move(removed,path);throw;}File.Delete(removed);return next;
    }

}
