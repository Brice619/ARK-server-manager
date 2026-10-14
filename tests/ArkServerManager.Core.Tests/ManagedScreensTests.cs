using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ManagedScreensTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"ark-managed-pages-"+Guid.NewGuid().ToString("N"));
    private ServerDefinition Server(ArkEdition edition=ArkEdition.ASE)=>new(Guid.NewGuid(),edition,"TheIsland",Path.Combine(root,"server"),new(){Name="Fixture"});
    private sealed class Stable(string directory):IStableServerState{public string Directory=>directory;public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
    private async Task Write(string path,string value){Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllTextAsync(path,value);}
    [Fact]
    public async Task ManualBackupVerifyRestoreDeletePreservesSettingsAndOtherArchives()
    {
        var server=Server();var saved=Path.Combine(server.Directory,"ShooterGame","Saved");var world=Path.Combine(saved,"SavedArks","world.ark");var player=Path.Combine(saved,"SaveGames","player.arkprofile");var ini=Path.Combine(saved,"Config","WindowsServer","Game.ini");
        await Write(world,"world-before");await Write(player,"player-before");await Write(ini,"settings-stay");var store=new ManagedBackupStore(server);
        var inventory=await store.CreateAsync("Before mod changes",false,_=>Task.FromResult<IStableServerState>(new Stable(saved)));var point=Assert.Single(inventory.Entries);Assert.True(point.Bytes>0);Assert.Equal(point,await store.VerifyAsync(point.Id));
        await Write(world,"world-after");await Write(player,"player-after");var restore=new WorldRestoreService(_=>Task.FromResult(true),_=>Task.CompletedTask,(change,ct)=>change(ct));var result=await restore.RestoreAsync(server,store.ArchivePath(point.Id),point.ArchiveNumber,Guid.NewGuid(),false);
        Assert.True(result.Restored);Assert.Equal("world-before",await File.ReadAllTextAsync(world));Assert.Equal("player-before",await File.ReadAllTextAsync(player));Assert.Equal("settings-stay",await File.ReadAllTextAsync(ini));
        var other=Path.Combine(store.DirectoryPath,"keep-me.txt");await Write(other,"unrelated");Assert.Empty((await store.DeleteAsync(point.Id)).Entries);Assert.True(File.Exists(other));Assert.True(File.Exists(world));
    }
    [Fact]
    public async Task DailyScheduleStartsNextOccurrenceAndRetentionLeavesManualBackups()
    {
        var server=Server();var saved=Path.Combine(server.Directory,"ShooterGame","Saved");await Write(Path.Combine(saved,"SavedArks","world.ark"),"world");var store=new ManagedBackupStore(server);var now=DateTimeOffset.Parse("2026-10-14T18:00:00Z");
        var scheduled=await store.ScheduleAsync(new(true,true,3,0,1,"UTC"),now:now);Assert.Null(ManagedBackupStore.DueDay(scheduled.Policy,now));Assert.Equal("2026-10-15",ManagedBackupStore.DueDay(scheduled.Policy,now.AddHours(9)));
        var before=await store.CreateAsync("Manual",false,_=>Task.FromResult<IStableServerState>(new Stable(saved)));var manual=Assert.Single(before.Entries);
        await store.CreateAsync("Scheduled 1",true,_=>Task.FromResult<IStableServerState>(new Stable(saved)));var last=await store.CreateAsync("Scheduled 2",true,_=>Task.FromResult<IStableServerState>(new Stable(saved)));
        Assert.Equal(2,last.Entries.Count);Assert.Contains(last.Entries,e=>e.Id==manual.Id);Assert.Single(last.Entries,e=>e.Kind=="Scheduled");Assert.True(File.Exists(store.ArchivePath(manual.Id)));
    }
    [Fact]
    public async Task PlayerAdminAndAllowlistUseDocumentedFilesAndQueueKeepsLatestBanIntent()
    {
        var server=Server();var store=new ManagedPlayerStore(root,server);const string id="76561198000004821";await store.SeedAsync([new("Brice",id,null)]);
        var admin=Path.Combine(server.Directory,"ShooterGame","Saved","AllowedCheaterSteamIDs.txt");await Write(admin,"76561198000007763\n");await store.EditAsync(id,"Admin",true);await store.EditAsync(id,"Allowlist",true);
        Assert.Contains(id,await File.ReadAllLinesAsync(admin));Assert.Contains("76561198000007763",await File.ReadAllLinesAsync(admin));Assert.Contains(id,await File.ReadAllLinesAsync(Path.Combine(server.Directory,"ShooterGame","Binaries",OperatingSystem.IsWindows()?"Win64":"Linux","PlayersExclusiveJoinList.txt")));
        await store.EditAsync(id,"Ban",true,queued:true);await store.EditAsync(id,"Ban",false,queued:true);await store.CompleteQueuedBanAsync(id,true);var pending=(await store.ReadAsync()).Players.Single(p=>p.AccountId==id);Assert.False(pending.Banned);Assert.True(pending.BanPending);await store.CompleteQueuedBanAsync(id,false);Assert.False((await store.ReadAsync()).Players.Single(p=>p.AccountId==id).BanPending);
        await Write(Path.Combine(server.Directory,"ShooterGame","Saved","SavedArks","76561198000002940.arkprofile"),"profile");Assert.Contains((await store.ReadAsync()).Players,p=>p.AccountId=="76561198000002940"&&p.Name=="Unknown player"&&p.LastSeen is null);
        var seen=DateTimeOffset.UtcNow;await store.ObserveAsync(new(server.Id,seen,[new("IslandRanger","76561198000002940")]));Assert.Contains((await store.ReadAsync()).Players,p=>p.Name=="IslandRanger"&&p.LastSeen==seen);
    }
    [Fact]
    public async Task AsaAdminFileAndInvalidRequestsAreHandledWithoutWritingOtherFiles()
    {
        var server=Server(ArkEdition.ASA);var store=new ManagedPlayerStore(root,server);const string id="0123456789abcdef0123456789abcdef";await store.SeedAsync([new("ASA player",id,null)]);await store.EditAsync(id,"Admin",true);Assert.True(File.Exists(Path.Combine(server.Directory,"ShooterGame","Saved","AllowedCheaterAccountIDs.txt")));Assert.False(File.Exists(Path.Combine(server.Directory,"ShooterGame","Saved","AllowedCheaterSteamIDs.txt")));
        await Assert.ThrowsAsync<ArgumentException>(()=>store.EditAsync("../wrong","Admin",true));Assert.Throws<ArgumentException>(()=>ManagedBackupStore.ValidatePolicy(new(true,true,24,0,7)));Assert.Throws<ArgumentException>(()=>ManagedBackupStore.ValidatePolicy(new(true,true,3,0,0)));
    }
    [Fact]
    public async Task LegacyBackupDeletionVerifiesOwnerAndPreservesOtherFiles()
    {
        var server=Server();var saved=Path.Combine(server.Directory,"ShooterGame","Saved");await Write(Path.Combine(saved,"SavedArks","world.ark"),"world");
        var service=new AutomaticBackupService(server,TimeSpan.FromSeconds(1),_=>Task.FromResult<IStableServerState>(new Stable(saved)),_=>Task.CompletedTask,SnapshotLayout.WorldSavedFolders);
        var state=await service.TickAsync(TimeSpan.FromHours(1),true,DateTimeOffset.UtcNow);var entry=Assert.Single(state.History);var other=Path.Combine(server.Directory,"backups","keep.txt");await Write(other,"unrelated");
        Assert.Empty((await AutomaticBackupService.DeleteRecordedAsync(server,entry.RuntimeHour)).History);Assert.True(File.Exists(other));Assert.False(File.Exists(Path.Combine(server.Directory,"backups",entry.FileName)));Assert.True(File.Exists(Path.Combine(saved,"SavedArks","world.ark")));
    }
    [Fact]
    public async Task ManagedDeletionRejectsAnArchiveReplacedWithAnotherServersBackup()
    {
        var server=Server();var saved=Path.Combine(server.Directory,"ShooterGame","Saved");await Write(Path.Combine(saved,"SavedArks","world.ark"),"world");var store=new ManagedBackupStore(server);var entry=Assert.Single((await store.CreateAsync("Owned",false,_=>Task.FromResult<IStableServerState>(new Stable(saved)))).Entries);
        var foreign=Path.Combine(root,"foreign.zip");await new SnapshotArchive().CreateAsync(saved,foreign,Guid.NewGuid(),server.Edition,entry.ArchiveNumber,layout:SnapshotLayout.WorldSavedFolders);File.Copy(foreign,store.ArchivePath(entry.Id),true);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.DeleteAsync(entry.Id));Assert.Single((await store.ReadAsync()).Entries);Assert.True(File.Exists(store.ArchivePath(entry.Id)));
    }
    [Fact]
    public async Task UnknownReportedAccountsDoNotBreakPlayerReadsOrEnterAdministrativeCache()
    {
        var server=Server();var store=new ManagedPlayerStore(root,server);var observation=new ServerPlayerQueryResult(server.Id,DateTimeOffset.UtcNow,[new("Unknown platform player","eos-account-42"),new("Verified player","76561198000004821")]);
        var cached=await store.ObserveAsync(observation);Assert.Equal("Verified player",Assert.Single(cached.Players).Name);Assert.Equal(2,observation.Players.Count);await Assert.ThrowsAsync<ArgumentException>(()=>store.EditAsync("eos-account-42","Ban",true));
    }
    public void Dispose(){var target=Path.GetFullPath(root);if(!target.StartsWith(Path.GetFullPath(Path.GetTempPath())+"ark-managed-pages-",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unexpected fixture cleanup path.");if(Directory.Exists(target))Directory.Delete(target,true);}
}
