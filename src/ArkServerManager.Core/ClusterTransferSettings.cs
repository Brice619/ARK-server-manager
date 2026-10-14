using System.Text.Json;
namespace ArkServerManager.Core;
public enum ClusterTransferOverride { Inherit, Allow, Block }
public sealed record ClusterTransferMap(Guid ServerId, ClusterTransferOverride[] Uploads, ClusterTransferOverride[] Downloads);
public sealed record ClusterTransferRules(bool[] Uploads, bool[] Downloads, List<ClusterTransferMap> Maps);
public sealed record ClusterTransferSource(Guid ServerId,string Directory,string SourceSHA256);
public sealed record ClusterTransferReview(Guid ClusterId, ClusterTransferRules Rules,List<ClusterTransferSource> Sources);
public sealed record ClusterTransferSave(ClusterTransferReview Expected,ClusterTransferRules Desired);
public sealed record ClusterTransferSaveResult(Guid ClusterId,int Applied,int Queued,string Message);
/// <summary>Transfer controls bind documented ARK GameUserSettings.ini ServerSettings keys.</summary>
public static class ClusterTransferSettings
{
    public static readonly string[] Subjects=["Survivors","Dinos","Items"];
    public static string Apply(string text,bool[] uploads,bool[] downloads)
    {
        ValidateValues(uploads);ValidateValues(downloads);var ini=new IniDocument(text);
        for(var i=0;i<3;i++){ini.SetValues("ServerSettings","PreventUpload"+Subjects[i],[(!uploads[i]).ToString()]);ini.SetValues("ServerSettings","PreventDownload"+Subjects[i],[(!downloads[i]).ToString()]);}
        // This master switch otherwise defeats all three download controls.
        ini.SetValues("ServerSettings","noTributeDownloads",[(!downloads.Any(v=>v)).ToString()]);return ini.ToString();
    }
    public static bool[] Read(string text,bool upload)
    {var ini=new IniDocument(text);var master=!upload&&bool.TryParse(ini.GetValues("ServerSettings","noTributeDownloads").LastOrDefault(),out var all)&&all;return Subjects.Select(s=>!master&&!(bool.TryParse(ini.GetValues("ServerSettings",(upload?"PreventUpload":"PreventDownload")+s).LastOrDefault(),out var block)&&block)).ToArray();}
    public static bool[] Effective(bool[] defaults,ClusterTransferOverride[] overrides)
    {ValidateValues(defaults);if(overrides is null||overrides.Length!=3||overrides.Any(v=>!Enum.IsDefined(v)))throw new ArgumentException("Choose supported transfer overrides.");return overrides.Select((v,i)=>v==ClusterTransferOverride.Inherit?defaults[i]:v==ClusterTransferOverride.Allow).ToArray();}
    public static void ValidateValues(bool[] values){if(values is null||values.Length!=3)throw new ArgumentException("Choose all three transfer categories.");}
    public static ClusterTransferRules Clone(ClusterTransferRules rules)=>new([..rules.Uploads],[..rules.Downloads],rules.Maps.Select(m=>new ClusterTransferMap(m.ServerId,[..m.Uploads],[..m.Downloads])).ToList());
}
public sealed class ClusterTransferSettingsOperations(CatalogRepository repository,string root,BackgroundOperations operations,PendingConfigurationEdits pending,
    Func<Func<IReadOnlyList<MemberOperationResult>,CancellationToken,Task>,CancellationToken,Task> withStatusScope,
    Func<Guid,CancellationToken,Task> requireNoConflicts,Func<Guid,CancellationToken,Task>? recordSaved=null)
{
    private AtomicJsonStore<ClusterTransferRules> Store(Guid id){if(id==Guid.Empty)throw new ArgumentException("Choose a cluster.");var path=Path.Combine(root,"app-settings","cluster-transfers",id.ToString("N")+".json");ManagedBackupStore.RejectLinks(path);return new(path,true);}
    public async Task<ClusterTransferReview> ReadAsync(Guid id,CancellationToken ct=default)
    {
        var catalog=await repository.ReadAsync(ct);var cluster=catalog.Clusters.Single(c=>c.Id==id);var members=catalog.Servers.Where(s=>s.ClusterId==id).ToArray();var saved=await Store(id).LoadAsync(ct);
        var defaultsText=cluster.Defaults?.UserSettings??"";var uploads=saved?.Uploads??ClusterTransferSettings.Read(defaultsText,true);var downloads=saved?.Downloads??ClusterTransferSettings.Read(defaultsText,false);ClusterTransferSettings.ValidateValues(uploads);ClusterTransferSettings.ValidateValues(downloads);
        var maps=new List<ClusterTransferMap>();var sources=new List<ClusterTransferSource>();
        foreach(var member in members){var file=await ServerConfigurationReader.ReadAsync(repository,new(member.Id,"GameUserSettings.ini"),ct);if(file.Text is null||file.SourceSHA256 is null)throw new InvalidOperationException("A map configuration is unavailable.");var savedMap=saved?.Maps.SingleOrDefault(m=>m.ServerId==member.Id);var effectiveText=file.Text;var queuedChanges=(await pending.ReadAsync(member.Id,ct)).Where(p=>p.FileName=="GameUserSettings.ini").ToArray();if(queuedChanges.Length==1)effectiveText=(await pending.ReviewAsync(new(member.Id,queuedChanges[0].Id),ct)).Text;var up=ClusterTransferSettings.Read(effectiveText,true);var down=ClusterTransferSettings.Read(effectiveText,false);
            ClusterTransferOverride[] ReadOverrides(bool[] values,bool[] baseline,ClusterTransferOverride[]? prior)=>values.Select((v,i)=>prior is {Length:3}&&ClusterTransferSettings.Effective(baseline,prior)[i]==v?prior[i]:v==baseline[i]?ClusterTransferOverride.Inherit:v?ClusterTransferOverride.Allow:ClusterTransferOverride.Block).ToArray();
            maps.Add(new(member.Id,ReadOverrides(up,uploads,savedMap?.Uploads),ReadOverrides(down,downloads,savedMap?.Downloads)));sources.Add(new(member.Id,member.Directory,file.SourceSHA256));}
        return new(id,new(uploads,downloads,maps),sources);
    }
    public BackgroundOperation Start(ManagerRequest request)
    {
        var save=request.Payload?.Deserialize<ClusterTransferSave>()??throw new ArgumentException("Load transfer settings before saving.");if(save.Expected is null||save.Desired is null||save.Expected.ClusterId==Guid.Empty)throw new ArgumentException("Load transfer settings before saving.");return operations.Submit(request.Id,"cluster-transfers-save",request.Payload,async(progress,ct)=>{
            var applied=0;var queued=0;await withStatusScope(async(statuses,token)=>{
                var current=await ReadAsync(save.Expected.ClusterId,token);if(JsonSerializer.Serialize(current)!=JsonSerializer.Serialize(save.Expected))throw new InvalidOperationException("Cluster maps or transfer settings changed. Reload before saving.");var rules=save.Desired;ClusterTransferSettings.ValidateValues(rules.Uploads);ClusterTransferSettings.ValidateValues(rules.Downloads);
                if(rules.Maps is null||rules.Maps.Count!=current.Sources.Count||rules.Maps.Select(m=>m.ServerId).Distinct().Count()!=rules.Maps.Count||rules.Maps.Any(m=>!current.Sources.Any(s=>s.ServerId==m.ServerId)))throw new ArgumentException("Choose each current cluster map once.");
                if(current.Sources.Count==0)throw new InvalidOperationException("Add a map before saving transfer rules.");var currentCatalog=await repository.ReadAsync(token);var targetCluster=currentCatalog.Clusters.Single(c=>c.Id==save.Expected.ClusterId);var presetSettings=targetCluster.Settings.Copy();if(presetSettings.GamePort==0)presetSettings=currentCatalog.Servers.First(s=>s.ClusterId==targetCluster.Id).Settings.Copy() with{Name=targetCluster.Name,MaxPlayers=targetCluster.Settings.MaxPlayers};var defaultPreset=targetCluster.Defaults??new ServerPreset(targetCluster.Name,targetCluster.Edition,presetSettings,[]);var desiredPreset=defaultPreset with{UserSettings=ClusterTransferSettings.Apply(defaultPreset.UserSettings??"",rules.Uploads,rules.Downloads)};PresetLibrary.ReadImport(JsonSerializer.SerializeToUtf8Bytes(new PresetExportDocument(1,desiredPreset)));var edits=new List<(ServerConfigurationEdit Edit,bool Running)>();foreach(var map in rules.Maps){var up=ClusterTransferSettings.Effective(rules.Uploads,map.Uploads);var down=ClusterTransferSettings.Effective(rules.Downloads,map.Downloads);var state=statuses.Single(s=>s.ServerId==map.ServerId);if(state.State is not(ServerState.Stopped or ServerState.Failed or ServerState.Running)||state.State==ServerState.Running&&!state.Managed)throw new InvalidOperationException("Wait for map actions or stop externally running maps before saving.");await requireNoConflicts(map.ServerId,token);if((await pending.ReadAsync(map.ServerId,token)).Count>0)throw new InvalidOperationException("A map has a saved configuration edit. Apply or resolve it before editing transfer rules.");var file=await ServerConfigurationReader.ReadAsync(repository,new(map.ServerId,"GameUserSettings.ini"),token);edits.Add((new(map.ServerId,"GameUserSettings.ini",file.SourceSHA256!,ClusterTransferSettings.Apply(file.Text!,up,down)),state.State==ServerState.Running));}
                // Each map uses the existing durable queue and verified stopped writer. Failed multi-map saves remain visible as pending edits for recovery.
                foreach(var item in edits){progress(new("Saving transfer rules for a cluster map."));await pending.EnqueueAsync(Guid.NewGuid(),item.Edit,token);if(item.Running)queued++;else{await pending.ApplyBeforeStartAsync(item.Edit.ServerId,token);applied++;}}
                await repository.ChangeAsync(catalog=>{var cluster=catalog.Clusters.Single(c=>c.Id==save.Expected.ClusterId);catalog.SetClusterDefaultPreset(cluster.Id,desiredPreset);},token);
                await Store(save.Expected.ClusterId).SaveAsync(ClusterTransferSettings.Clone(rules),token);
            },ct);if(recordSaved is not null)await recordSaved(save.Expected.ClusterId,ct);return new ClusterTransferSaveResult(save.Expected.ClusterId,applied,queued,queued>0?$"Transfer rules saved. {queued} maps apply on next start.":"Transfer rules saved; they take effect on next start.");});
    }
}
