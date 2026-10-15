using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    public static async Task CheckStartupDispatchForReviewAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"ark-start-dispatch-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        using var stop=new CancellationTokenSource();MainWindow? window=null;Task? serving=null;
        var lifecycleRequests=0;var backupRequests=0;
        try
        {
            var id=Guid.NewGuid();
            var host=new LocalManagerServer(root,(request,_)=>
            {
                if(request.Operation=="windows-backup-prepare"){backupRequests++;throw new IOException("Backup helper deliberately unavailable.");}
                if(request.Operation!="lifecycle-execute-member")throw new InvalidOperationException("Unexpected startup prerequisite: "+request.Operation);
                lifecycleRequests++;
                // This verifies dispatch only. No game or substitute server is launched.
                return Task.FromResult<object>(new BackgroundOperation(request.Id,"server-member-lifecycle",BackgroundOperationState.Failed,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,null,null,"Isolated dispatch reached the lifecycle handler."));
            });
            serving=host.RunAsync(stop.Token);window=new MainWindow(["--data-root",root]);
            foreach(var action in new[]{LifecycleAction.Start,LifecycleAction.Restart})
            {
                await window.RunLifecycleActionAsync(id,action,true);
                if(window.pendingLifecycle is not null||window.lifecycleNotice!="Isolated dispatch reached the lifecycle handler.")throw new InvalidOperationException("Startup was blocked before reaching the lifecycle handler.");
            }
            if(lifecycleRequests!=2||backupRequests!=0)throw new InvalidOperationException("Start/restart must not depend on backup-helper preparation.");
        }
        finally{window?.lifetime.Cancel();stop.Cancel();if(serving is not null)try{await serving;}catch(OperationCanceledException){}Directory.Delete(root,true);}
    }
    public static async Task CheckNetworkAccessSaveForReviewAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"ark-network-save-review-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var stop=new CancellationTokenSource();
        MainWindow? window=null;
        Task? serving=null;
        try
        {
            var repository=new CatalogRepository(Path.Combine(root,"catalog.json"));
            var definition=new ServerDefinition(Guid.NewGuid(),ArkEdition.ASE,"TheIsland",root,new(){Name="Network review",GamePort=7777,QueryPort=27015,RconPort=32330});
            var executable=Path.Combine(root,"ShooterGame","Binaries","Win64","ShooterGameServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);await File.WriteAllTextAsync(executable,"isolated firewall fixture");
            await repository.ChangeAsync(c=>c.AddServer(definition));
            await using var jobs=new BackgroundOperations(16,1);
            var coordinator=new ServerLifecycleCoordinator(repository,_=>throw new InvalidOperationException(),(_,_)=>Task.CompletedTask);
            var scripts=new List<string>();var fail=false;var preferenceSaves=0;
            var firewall=new WindowsFirewallManagerOperations(repository,jobs,coordinator,(script,_)=>{scripts.Add(script);return Task.FromResult(fail?1:0);},root);
            var host=new LocalManagerServer(root,async(request,ct)=>request.Operation switch
            {
                "server-launch-preferences-read"=>await ServerLaunchPreferencesStore.ReadAsync(repository,request.Payload!.Value.GetGuid(),ct),
                "server-launch-preferences-save"=>await SavePreferences(request,ct),
                "server-firewall-setup-start"=>firewall.Start(request),
                "operation-status-lookup"=>new OperationLookup(jobs.Find(request.Payload!.Value.Deserialize<OperationStatusRequest>()!.Id)),
                "server-firewall-recorded"=>await firewall.ReadRecordedAsync(request.Payload!.Value.GetGuid(),ct),
                "server-configuration-read"=>ReadConfiguration(request),
                _=>throw new InvalidOperationException("Unexpected fixture operation: "+request.Operation)
            });
            async Task<ServerLaunchPreferencesState> SavePreferences(ManagerRequest request,CancellationToken ct)
            {preferenceSaves++;return await ServerLaunchPreferencesStore.SaveAsync(repository,request.Payload!.Value.Deserialize<ServerLaunchPreferencesEdit>()!,ct);}
            ServerConfigurationFile ReadConfiguration(ManagerRequest request)
            {var selection=request.Payload!.Value.Deserialize<ServerConfigurationReadRequest>()!;return new(definition.Id,selection.FileName,"Windows",null,[]);}
            serving=host.RunAsync(stop.Token);
            window=new MainWindow(["--data-root",root]);window.catalog=await repository.ReadAsync();
            await window.LoadScreenSettingsAsync(definition);window.screenSettingDrafts[definition.Id]=[];
            foreach(var scope in new[]{ServerNetworkScope.Internet,ServerNetworkScope.LocalNetwork})
            {
                window.networkScopeDrafts[definition.Id]=scope;
                await window.SaveScreenSettingsAsync(definition);
                var actual=(await repository.ReadAsync()).Servers.Single();
                if(actual.NetworkScope!=scope||window.networkScopeDrafts.ContainsKey(definition.Id)||!scripts.Last().Contains("-RemoteAddress "+(scope==ServerNetworkScope.Internet?"'Any'":"'LocalSubnet'")))
                    throw new InvalidOperationException("The settings page did not apply and persist its network choice: "+window.settingSaveNotice+"; firewall="+window.networkAddressNotice+"; saved="+actual.NetworkScope+"; scripts="+scripts.Count);
            }
            fail=true;window.networkScopeDrafts[definition.Id]=ServerNetworkScope.Internet;
            await window.SaveScreenSettingsAsync(definition);
            if(preferenceSaves!=2||(await repository.ReadAsync()).Servers.Single().NetworkScope!=ServerNetworkScope.LocalNetwork||!window.networkScopeDrafts.ContainsKey(definition.Id))
                throw new InvalidOperationException("Failed firewall changes must retain the draft and leave the saved choice unchanged.");
        }
        finally
        {
            window?.lifetime.Cancel();stop.Cancel();if(serving is not null)try{await serving;}catch(OperationCanceledException){}
            Directory.Delete(root,true);
        }
    }
    public string ManagementFixtureDirectoryForReview=>designPreview?ManagementReviewServer(ReviewServer()).Directory:throw new InvalidOperationException();
    public bool ManagementPageReadyForReview=>designPreview&&approvedBackupStates.TryGetValue(ReviewServerId,out var state)&&state.Inventory is not null&&!state.Loading&&!state.Busy;
    public bool ManagementPlayerReadyForReview=>designPreview&&approvedPlayerStates.TryGetValue(ReviewServerId,out var state)&&state.Inventory is not null&&!state.Loading&&!state.Busy;
    private void SeedApprovedManagementReview()
    {
        if(!designPreview)throw new InvalidOperationException();
        var date=DateTimeOffset.Parse("2026-10-13T08:30:00-04:00");
        approvedBackupStates[ReviewServerId]=new(){Inventory=new(ReviewServerId,[new(Guid.NewGuid(),"Before mod changes","Manual",date,420*1024*1024,1),new(Guid.NewGuid(),"Manual backup","Manual",date.AddDays(-1).AddHours(12.5),415*1024*1024,2),new(Guid.NewGuid(),"Scheduled backup","Scheduled",date.AddDays(-1).AddHours(9.5),410*1024*1024,3)],new(true,true,3,0,7,TimeZoneInfo.Local.Id))};
        playerCategory=1;administrationServer=ReviewServerId;
        approvedPlayerStates[ReviewServerId]=new(){Selected="76561198000004821",Inventory=new(ReviewServerId,[new("Brice","76561198000004821",date.AddDays(-1)),new("SurvivorTwo","76561198000007763",date.AddDays(-1),Admin:true),new("IslandRanger","76561198000002940",date.AddDays(-1))])};
        commandConsoles[ReviewServerId]=new(){LogLines=["[2025.07.15 12:14:03] [Manager] Server setup completed","[2025.07.15 12:14:04] [Manager] Settings saved","[2025.07.15 12:14:07] [Init] Loading configuration...","[2025.07.15 12:14:08] [Init] Configuration loaded","[2025.07.15 12:14:10] [Mods] Found 5 mods","[2025.07.15 12:14:12] [World] TheIsland_WP loaded","[2025.07.15 12:14:13] [Manager] Server stopped","[2025.07.15 12:14:13] [Manager] Saving world...","[2025.07.15 12:14:15] [Save] World saved successfully","[2025.07.15 12:14:15] [Manager] Shutdown complete"]};
    }
}
