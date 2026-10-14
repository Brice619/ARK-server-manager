using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record LegacyBackupSelection(Guid ServerId,long RuntimeHour,bool Confirmed=false);

public sealed class ManagedScreensOperations(CatalogRepository repository,string root,BackgroundOperations operations,ServerLifecycleCoordinator lifecycle,
    Func<ServerDefinition,string,bool,CancellationToken,Task<ManagedBackupInventory>> createBackup,
    Func<ServerDefinition,CancellationToken,Task<InstallationStopCheck>> verifyStopped)
{
    private async Task<ServerDefinition> ServerAsync(Guid id,CancellationToken ct)=>(await repository.ReadAsync(ct)).Servers.Single(s=>s.Id==id);
    public async Task<ManagedBackupInventory> ReadBackupsAsync(Guid id,CancellationToken ct)=>await new ManagedBackupStore(await ServerAsync(id,ct)).ReadAsync(ct);
    public async Task<ManagedPlayerInventory> ReadPlayersAsync(Guid id,CancellationToken ct)=>await new ManagedPlayerStore(root,await ServerAsync(id,ct)).ReadAsync(ct);
    public BackgroundOperation Backup(ManagerRequest request,string action)
    {
        return operations.Submit(request.Id,"managed-backup-"+action,request.Payload,async (progress,ct)=>
        {
            if(action=="delete-legacy"){var input=request.Payload!.Value.Deserialize<LegacyBackupSelection>()!;if(!input.Confirmed)throw new ArgumentException("Confirm backup deletion.");return (object)await AutomaticBackupService.DeleteRecordedAsync(await ServerAsync(input.ServerId,ct),input.RuntimeHour,ct);}
            if(action=="create"){var input=request.Payload!.Value.Deserialize<ManagedBackupCreate>()!;progress(new("Creating and verifying world/player backup."));return (object)await createBackup(await ServerAsync(input.ServerId,ct),input.Name,false,ct);}
            if(action=="schedule"){var input=request.Payload!.Value.Deserialize<ManagedBackupSchedule>()!;return await new ManagedBackupStore(await ServerAsync(input.ServerId,ct)).ScheduleAsync(input.Policy,ct);}
            var selection=request.Payload!.Value.Deserialize<ManagedBackupSelection>()!;if(!selection.Confirmed)throw new ArgumentException("Confirm the selected backup action.");var server=await ServerAsync(selection.ServerId,ct);var store=new ManagedBackupStore(server);
            if(action=="delete")return await store.DeleteAsync(selection.BackupId,ct);
            if(action!="restore")throw new ArgumentException("Unsupported backup action.");
            var point=await store.VerifyAsync(selection.BackupId,ct);progress(new("Verifying backup and stopping only this map."));
            var service=new WorldRestoreService(async token=>(await lifecycle.ExecuteMemberAsync(server.Id,LifecycleAction.Stop,token)).Success,
                async token=>{if(!(await lifecycle.ExecuteMemberAsync(server.Id,LifecycleAction.Start,token)).Success)throw new InvalidOperationException("The restored map did not start.");},
                (change,token)=>lifecycle.WithStoppedMemberAsync(server,async inner=>{if(server.ClusterId is {} cluster)await ClusterRestoreRecords.RequireConsistentAsync(await repository.ReadAsync(inner),cluster,root,inner);await change(inner);},verifyStopped,token));
            return await service.RestoreAsync(server,store.ArchivePath(point.Id),point.ArchiveNumber,request.Id,false,ct);
        });
    }
    public BackgroundOperation Player(ManagerRequest request)
    {
        var input=request.Payload!.Value.Deserialize<ManagedPlayerEdit>()!;ManagedPlayerStore.ValidateAccount(input.AccountId);
        if(input.Action is not("Kick" or "Ban" or "Admin" or "Allowlist"))throw new ArgumentException("Unsupported player action.");
        return operations.Submit(request.Id,"managed-player-edit",request.Payload,async (_,ct)=>
        {
            var server=await ServerAsync(input.ServerId,ct);var store=new ManagedPlayerStore(root,server);var running=(await lifecycle.StatusAsync(ct)).Any(s=>s.ServerId==server.Id&&s.State==ServerState.Running);
            if(input.Action=="Kick"||input.Action=="Ban"&&running)
            {
                if(!running)throw new InvalidOperationException("The server is stopped; no player can be kicked.");
                var endpoint=await ServerRconConfiguration.ReadAsync(repository,server.Id,TimeSpan.FromSeconds(5),RconResponseMode.SinglePacket,ct);
                var command=input.Action=="Kick"?"KickPlayer":input.Enabled?"BanPlayer":"UnbanPlayer";await new RconClient().ExecuteAsync(endpoint,command+" "+input.AccountId,ct);
                return input.Action=="Ban"?await store.EditAsync(input.AccountId,"Ban",input.Enabled,ct:ct):await store.ReadAsync(ct);
            }
            if(input.Action=="Ban")return await store.EditAsync(input.AccountId,"Ban",input.Enabled,queued:true,ct:ct);
            ManagedPlayerInventory? result=null;await lifecycle.WithStoppedMemberAsync(server,async token=>result=await store.EditAsync(input.AccountId,input.Action,input.Enabled,ct:token),verifyStopped,ct);return result!;
        });
    }
    public async Task ApplyPendingBansAsync(CancellationToken ct)
    {
        var statuses=await lifecycle.StatusAsync(ct);foreach(var server in (await repository.ReadAsync(ct)).Servers.Where(s=>statuses.Any(x=>x.ServerId==s.Id&&x.State==ServerState.Running)))
        {
            var store=new ManagedPlayerStore(root,server);foreach(var player in (await store.ReadAsync(ct)).Players.Where(p=>p.BanPending))
            {
                try{var endpoint=await ServerRconConfiguration.ReadAsync(repository,server.Id,TimeSpan.FromSeconds(5),RconResponseMode.SinglePacket,ct);await new RconClient().ExecuteAsync(endpoint,(player.Banned?"BanPlayer":"UnbanPlayer")+" "+player.AccountId,ct);await store.CompleteQueuedBanAsync(player.AccountId,player.Banned,ct);}
                catch(Exception error)when(error is IOException or TimeoutException or InvalidOperationException or ArgumentException){break;}
            }
        }
    }
}
