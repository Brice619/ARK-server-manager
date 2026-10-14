using System.Text.Json;
namespace ArkServerManager.Core;
public sealed class ManagedClusterBackupOperations(CatalogRepository repository,string root,BackgroundOperations operations,ServerLifecycleCoordinator lifecycle,Func<Guid,string,bool,CancellationToken,Task<ManagedClusterBackupInventory>> create,Func<ServerDefinition,CancellationToken,Task<InstallationStopCheck>> verifyStopped,Func<Guid,string,CancellationToken,Task> record)
{
    public async Task<ManagedClusterBackupInventory> ReadAsync(Guid id,CancellationToken ct)=>await new ManagedClusterBackupStore(await repository.ReadAsync(ct),id,root).ReadAsync(ct);
    public BackgroundOperation Start(ManagerRequest request,string action)=>operations.Submit(request.Id,"managed-cluster-backup-"+action,request.Payload,async(progress,ct)=>
    {
        if(action=="create"){var input=request.Payload!.Value.Deserialize<ManagedClusterBackupCreate>()!;var result=await create(input.ClusterId,input.Name,false,ct);await record(input.ClusterId,"Cluster backup created",ct);return (object)await ReadAsync(input.ClusterId,ct);}
        if(action=="preferences"){var input=request.Payload!.Value.Deserialize<ManagedClusterBackupPreferences>()!;var result=await new ManagedClusterBackupStore(await repository.ReadAsync(ct),input.ClusterId,root).SavePreferencesAsync(input,ct);await record(input.ClusterId,"Cluster backup schedule/scope saved",ct);return await ReadAsync(input.ClusterId,ct);}
        var selected=request.Payload!.Value.Deserialize<ManagedClusterBackupSelection>()!;if(!selected.Confirmed)throw new ArgumentException("Confirm the cluster backup action.");
        if(action=="delete"){await lifecycle.WithStatusScopeAsync(async(_,token)=>{await new ManagedClusterBackupStore(await repository.ReadAsync(token),selected.ClusterId,root).DeleteAsync(selected.BackupId,token);},ct);await record(selected.ClusterId,"Cluster backup deleted",ct);return await ReadAsync(selected.ClusterId,ct);}
        if(action!="restore")throw new ArgumentException("Unsupported cluster backup action.");
        ClusterRestoreRecord? restored=null;progress(new("Checking that cluster maps are stopped and verifying the selected archives."));
        await lifecycle.WithStoppedClusterAsync(selected.ClusterId,root,async(catalog,token)=>
        {
            var store=new ManagedClusterBackupStore(catalog,selected.ClusterId,root);var prepared=await store.PrepareRestoreAsync(selected.BackupId,token);
            await ClusterRestoreStaging.StagePreparedAsync(catalog,root,prepared,request.Id,true,token);
            progress(new("Restoring the captured maps and included transfer data. Configuration files are preserved."));restored=await ClusterRestoreReplacement.ReplaceAsync(catalog,selected.ClusterId,root,request.Id,token);
        },verifyStopped,ct);
        await record(selected.ClusterId,"Cluster backup restored",ct);return restored??throw new InvalidOperationException("Cluster restoration did not complete.");
    });
}
