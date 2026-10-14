using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ArkServerManager.Core;

public sealed record ManagedPlayer(string Name,string AccountId,DateTimeOffset? LastSeen,bool Admin=false,bool Allowlisted=false,bool Banned=false,bool Online=false,bool BanPending=false);
public sealed record ManagedPlayerInventory(Guid ServerId,List<ManagedPlayer> Players);
public sealed record ManagedPlayerEdit(Guid ServerId,string AccountId,string Action,bool Enabled);
public sealed class ManagedPlayerStore(string managerRoot,ServerDefinition server)
{
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> Gates=new(StringComparer.OrdinalIgnoreCase);
    private string PathName=>Path.Combine(managerRoot,"app-settings","players",server.Id.ToString("N")+".json");
    private SemaphoreSlim Gate=>Gates.GetOrAdd(Path.GetFullPath(PathName),_=>new(1,1));
    private AtomicJsonStore<ManagedPlayerInventory> Store=>new(PathName,true);
    public static void ValidateAccount(string id){if(!Regex.IsMatch(id,@"\A(?:[0-9]{17}|[a-fA-F0-9]{32})\z"))throw new ArgumentException("Choose a valid Steam or EOS account ID.");}
    private string AdminPath=>Path.Combine(server.Directory,"ShooterGame","Saved",server.Edition==ArkEdition.ASA?"AllowedCheaterAccountIDs.txt":"AllowedCheaterSteamIDs.txt");
    private string AllowPath=>Path.Combine(server.Directory,"ShooterGame","Binaries",OperatingSystem.IsWindows()?"Win64":"Linux","PlayersExclusiveJoinList.txt");
    private static async Task<HashSet<string>> ReadIdsAsync(string path,CancellationToken ct){ManagedBackupStore.RejectLinks(path);return File.Exists(path)?new(await File.ReadAllLinesAsync(path,ct),StringComparer.Ordinal):[];}
    private async Task<ManagedPlayerInventory> LoadAsync(CancellationToken ct)
    {
        ManagedBackupStore.RejectLinks(PathName);var state=await Store.LoadAsync(ct)??new(server.Id,[]);if(state.ServerId!=server.Id||state.Players is null||state.Players.Any(p=>p is null||string.IsNullOrWhiteSpace(p.Name)||p.AccountId is null||!Regex.IsMatch(p.AccountId,@"\A(?:[0-9]{17}|[a-fA-F0-9]{32})\z"))||state.Players.Select(p=>p.AccountId).Distinct().Count()!=state.Players.Count)throw new InvalidDataException("Player history is invalid.");
        var admins=await ReadIdsAsync(AdminPath,ct);var allow=await ReadIdsAsync(AllowPath,ct);
        foreach(var folder in new[]{"SavedArks","SaveGames"})
        {
            var saves=Path.Combine(server.Directory,"ShooterGame","Saved",folder);ManagedBackupStore.RejectLinks(saves);if(!Directory.Exists(saves))continue;
            var options=new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint,IgnoreInaccessible=false};
            foreach(var profile in Directory.EnumerateFiles(saves,"*.arkprofile",options).Take(10000))
            {var id=Path.GetFileNameWithoutExtension(profile);if(Regex.IsMatch(id,@"\A(?:[0-9]{17}|[a-fA-F0-9]{32})\z")&&!state.Players.Any(p=>p.AccountId==id))state.Players.Add(new("Unknown player",id,null));}
        }
        foreach(var id in admins.Concat(allow).Where(id=>Regex.IsMatch(id,@"\A(?:[0-9]{17}|[a-fA-F0-9]{32})\z")).Distinct())if(!state.Players.Any(p=>p.AccountId==id))state.Players.Add(new("Unknown player",id,null));
        return state with{Players=state.Players.Select(p=>p with{Admin=admins.Contains(p.AccountId),Allowlisted=allow.Contains(p.AccountId),Online=false}).ToList()};
    }
    public async Task<ManagedPlayerInventory> ReadAsync(CancellationToken ct=default){await Gate.WaitAsync(ct);try{return await LoadAsync(ct);}finally{Gate.Release();}}
    public async Task<ManagedPlayerInventory> ObserveAsync(ServerPlayerQueryResult observed,CancellationToken ct=default)
    {
        if(observed.ServerId!=server.Id)throw new ArgumentException("Player observation belongs to another server.");await Gate.WaitAsync(ct);try{var state=await LoadAsync(ct);foreach(var p in observed.Players){if(!Regex.IsMatch(p.AccountId,@"\A(?:[0-9]{17}|[a-fA-F0-9]{32})\z"))continue;var old=state.Players.FirstOrDefault(x=>x.AccountId==p.AccountId);state.Players.RemoveAll(x=>x.AccountId==p.AccountId);state.Players.Add(old is null?new(p.Name,p.AccountId,observed.MeasuredAt,Online:true):old with{Name=p.Name,LastSeen=observed.MeasuredAt,Online=true});}await Store.SaveAsync(state,ct);return state;}finally{Gate.Release();}
    }
    public async Task<ManagedPlayerInventory> SeedAsync(IEnumerable<ManagedPlayer> players,CancellationToken ct=default){await Gate.WaitAsync(ct);try{var old=await Store.LoadAsync(ct);if(old is not null)return await LoadAsync(ct);var state=new ManagedPlayerInventory(server.Id,players.ToList());foreach(var p in state.Players)ValidateAccount(p.AccountId);await Store.SaveAsync(state,ct);return state;}finally{Gate.Release();}}
    public async Task<ManagedPlayerInventory> EditAsync(string id,string action,bool enabled,bool queued=false,CancellationToken ct=default)
    {
        ValidateAccount(id);if(action is not("Admin" or "Allowlist" or "Ban"))throw new ArgumentException("Unsupported player action.");await Gate.WaitAsync(ct);try
        {
            var state=await LoadAsync(ct);var selected=state.Players.Single(p=>p.AccountId==id);
            if(action is "Admin" or "Allowlist")
            {
                var path=action=="Admin"?AdminPath:AllowPath;var lines=await ReadIdsAsync(path,ct);if(enabled)lines.Add(id);else lines.Remove(id);Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{await File.WriteAllLinesAsync(temporary,lines.Order(StringComparer.Ordinal),ct);File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
                selected=action=="Admin"?selected with{Admin=enabled}:selected with{Allowlisted=enabled};
            }
            else selected=selected with{Banned=enabled,BanPending=queued};
            state.Players.RemoveAll(p=>p.AccountId==id);state.Players.Add(selected);await Store.SaveAsync(state,ct);return state;
        }finally{Gate.Release();}
    }
    public async Task CompleteQueuedBanAsync(string id,bool expected,CancellationToken ct=default)
    {
        await Gate.WaitAsync(ct);try{var state=await LoadAsync(ct);var player=state.Players.Single(p=>p.AccountId==id);if(player.BanPending&&player.Banned==expected){state.Players.Remove(player);state.Players.Add(player with{BanPending=false});await Store.SaveAsync(state,ct);}}finally{Gate.Release();}
    }
}
