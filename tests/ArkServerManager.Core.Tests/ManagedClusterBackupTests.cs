using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class ManagedClusterBackupTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"ark-managed-cluster-"+Guid.NewGuid().ToString("N"));
    private sealed class Runtime:IServerRuntime{public ServerState State=>ServerState.Stopped;public Task StartAsync(ServerDefinition s,CancellationToken ct)=>Task.CompletedTask;public Task<bool> StopAsync(CancellationToken ct)=>Task.FromResult(true);}
    private async Task Write(string path,string text){Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllTextAsync(path,text);}
    [Theory]
    [InlineData(true,true)]
    [InlineData(false,false)]
    [InlineData(false,true)]
    public async Task SelectedWorldsAndOptionalSharedDataRestoreTogetherAndRecoverWithoutTouchingSettings(bool allMaps,bool shared)
    {
        var id=Guid.NewGuid();var first=new ServerDefinition(Guid.NewGuid(),ArkEdition.ASE,"TheIsland",Path.Combine(root,"first"),new(){Name="First",GamePort=7777,QueryPort=27015,RconPort=32330},id);var second=first with{Id=Guid.NewGuid(),Directory=Path.Combine(root,"second"),Map="ScorchedEarth",Settings=new(){Name="Second",GamePort=7787,QueryPort=27025,RconPort=32340}};var catalog=new CatalogDocument(1,[new(id,"Cluster",ArkEdition.ASE,new())],[first,second]);var repository=new CatalogRepository(Path.Combine(root,"catalog.json"));await repository.ChangeAsync(c=>{c.AddCluster(catalog.Clusters[0]);c.AddServer(first);c.AddServer(second);});
        foreach(var server in catalog.Servers){await Write(Path.Combine(server.Directory,"ShooterGame","Saved","SavedArks","world.ark"),"original-"+server.Id);await Write(Path.Combine(server.Directory,"ShooterGame","Saved","Config","WindowsServer","Game.ini"),"keep-settings");}
        var storage=ManagedClusterStorage.Prepare(catalog,id,root);await Write(Path.Combine(storage.Transfers,"player.arkprofile"),"shared-original");var coordinator=new ServerLifecycleCoordinator(repository,_=>new Runtime(),(_,_)=>Task.CompletedTask);Task<InstallationStopCheck> StopCheck(ServerDefinition s,CancellationToken ct)=>Task.FromResult(new InstallationStopCheck(s.Directory,InstallationStopState.Stopped,[],[],DateTimeOffset.UtcNow));
        var store=new ManagedClusterBackupStore(catalog,id,root);await store.SavePreferencesAsync(new(id,new(true,false,3,0,1),allMaps?[first.Id,second.Id]:[first.Id],shared));
        var entry=Assert.Single((await store.CreateAsync("Before mods",false,ct=>coordinator.AcquireClusterBackupStateAsync(id,root,(_,_)=>Task.CompletedTask,(_,_)=>throw new InvalidOperationException("Stopped capture must not use live provider"),StopCheck,ct))).Entries);Assert.Equal(allMaps?2:1,entry.Point.Maps.Count);Assert.Equal(shared,entry.IncludesShared);Assert.True(entry.Bytes>0);
        foreach(var server in catalog.Servers)await Write(Path.Combine(server.Directory,"ShooterGame","Saved","SavedArks","world.ark"),"after-"+server.Id);await Write(Path.Combine(storage.Transfers,"player.arkprofile"),"shared-after");
        var prepared=await store.PrepareRestoreAsync(entry.Id);var operation=Guid.NewGuid();await ClusterRestoreStaging.StagePreparedAsync(catalog,root,prepared,operation,true);var restored=await ClusterRestoreReplacement.ReplaceAsync(catalog,id,root,operation);Assert.Equal(ClusterRestorePhase.Restored,restored.Phase);
        Assert.Equal("original-"+first.Id,await File.ReadAllTextAsync(Path.Combine(first.Directory,"ShooterGame","Saved","SavedArks","world.ark")));Assert.Equal((allMaps?"original-":"after-")+second.Id,await File.ReadAllTextAsync(Path.Combine(second.Directory,"ShooterGame","Saved","SavedArks","world.ark")));Assert.Equal(shared?"shared-original":"shared-after",await File.ReadAllTextAsync(Path.Combine(storage.Transfers,"player.arkprofile")));
        foreach(var server in catalog.Servers)Assert.Equal("keep-settings",await File.ReadAllTextAsync(Path.Combine(server.Directory,"ShooterGame","Saved","Config","WindowsServer","Game.ini")));
        var journal=Path.Combine(storage.Backups,"cluster-restore-transactions",operation.ToString("N")+".json");await new AtomicJsonStore<ClusterRestoreRecord>(journal).SaveAsync(restored with{Phase=ClusterRestorePhase.Replacing});await ClusterRestoreRecovery.RollbackAsync(catalog,id,root,operation);Assert.Equal("after-"+first.Id,await File.ReadAllTextAsync(Path.Combine(first.Directory,"ShooterGame","Saved","SavedArks","world.ark")));Assert.Equal("shared-after",await File.ReadAllTextAsync(Path.Combine(storage.Transfers,"player.arkprofile")));
        if(allMaps&&shared)
        {
            var point=await new CoordinatedArchivePublisher().PublishAsync(catalog,id,root,1,ct=>coordinator.AcquireClusterBackupStateAsync(id,root,(_,_)=>Task.CompletedTask,(_,_)=>throw new InvalidOperationException(),StopCheck,ct));
            await new AtomicJsonStore<CoordinatedBackupSchedule>(Path.Combine(storage.Backups,"Coordinated","schedule.json")).SaveAsync(new(1,id,ArkEdition.ASE,TimeSpan.TicksPerHour,null,[],null,null,[point]));
            var legacy=Assert.Single((await store.ReadAsync()).Entries,e=>e.Legacy);Assert.Equal(point.Capture.Id,legacy.Id);await store.VerifyAsync(legacy.Id);await store.DeleteAsync(legacy.Id);Assert.DoesNotContain((await store.ReadAsync()).Entries,e=>e.Legacy);Assert.True(File.Exists(store.ArchivePath(entry,first.Id)));
        }
        Assert.Empty((await store.DeleteAsync(entry.Id)).Entries);Assert.False(File.Exists(store.ArchivePath(entry,first.Id)));Assert.Throws<ArgumentException>(()=>store.SavePreferencesAsync(new(id,new(),[],true)).GetAwaiter().GetResult());
    }
    public void Dispose(){var target=Path.GetFullPath(root);if(!target.StartsWith(Path.GetFullPath(Path.GetTempPath())+"ark-managed-cluster-",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unexpected fixture path.");if(Directory.Exists(target))Directory.Delete(target,true);}
}
