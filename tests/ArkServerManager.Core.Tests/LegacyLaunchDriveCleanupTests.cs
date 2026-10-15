using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class LegacyLaunchDriveCleanupTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"ark-legacy-drive-cleanup-"+Guid.NewGuid().ToString("N"));
    private string Record=>Path.Combine(root,"drives.json");
    private sealed class Backend : ILegacyWindowsLaunchDriveBackend
    {
        public Dictionary<char,List<string>> Targets=[];
        public List<char> Removed=[];
        public IReadOnlyList<string> Query(char letter)=>Targets.GetValueOrDefault(letter)??[];
        public void RemoveExact(char letter,string target)
        {Assert.Equal([target],Query(letter));Targets.Remove(letter);Removed.Add(letter);}
    }
    private async Task<WindowsLaunchDrive> RecordOwnedAsync(Backend backend,LaunchDrivePhase phase=LaunchDrivePhase.Active)
    {
        var entry=new WindowsLaunchDrive(Guid.NewGuid(),'Z',root,@"\??\"+root,phase);
        backend.Targets['Z']=[entry.Target];
        await new AtomicJsonStore<WindowsLaunchDriveDocument>(Record).SaveAsync(new(1,[entry]));
        return entry;
    }
    [Fact]
    public async Task RemovesOnlyOwnedStoppedMappingAndPreservesOtherDrives()
    {
        var backend=new Backend();await RecordOwnedAsync(backend);backend.Targets['Y']=["foreign target"];
        var stopped=false;var cleanup=new LegacyWindowsLaunchDriveCleanup(Record,backend,(_,_)=>Task.FromResult(stopped));
        await cleanup.CleanupStoppedAsync();Assert.Empty(backend.Removed);
        stopped=true;await cleanup.CleanupStoppedAsync();Assert.Equal(['Z'],backend.Removed);
        Assert.Equal(["foreign target"],backend.Query('Y'));
        Assert.Empty((await new AtomicJsonStore<WindowsLaunchDriveDocument>(Record).LoadAsync())!.Drives);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedAndStackedTargetsArePreserved(bool stacked)
    {
        var backend=new Backend();var owned=await RecordOwnedAsync(backend);
        backend.Targets['Z']=stacked?["foreign target",owned.Target]:["foreign target"];
        await new LegacyWindowsLaunchDriveCleanup(Record,backend,(_,_)=>Task.FromResult(true)).CleanupStoppedAsync();
        Assert.Empty(backend.Removed);Assert.NotEmpty(backend.Query('Z'));
        Assert.Single((await new AtomicJsonStore<WindowsLaunchDriveDocument>(Record).LoadAsync())!.Drives);
    }
    [Fact]
    public async Task UncertainPreparedMappingIsPreserved()
    {
        var backend=new Backend();await RecordOwnedAsync(backend,LaunchDrivePhase.Prepared);
        await new LegacyWindowsLaunchDriveCleanup(Record,backend,(_,_)=>Task.FromResult(true)).CleanupStoppedAsync();
        Assert.Empty(backend.Removed);Assert.NotEmpty(backend.Query('Z'));
    }
    [Fact]
    public async Task AbsentMappingRetiresItsRecordWithoutCreatingAnother()
    {
        var backend=new Backend();await RecordOwnedAsync(backend);backend.Targets.Clear();
        await new LegacyWindowsLaunchDriveCleanup(Record,backend,(_,_)=>Task.FromResult(true)).CleanupStoppedAsync();
        Assert.Empty(backend.Removed);Assert.Empty(backend.Targets);
        Assert.Empty((await new AtomicJsonStore<WindowsLaunchDriveDocument>(Record).LoadAsync())!.Drives);
    }
    [Fact]
    public async Task NewInstallationDoesNotCreateAMappingOrRecord()
    {
        var backend=new Backend();
        await new LegacyWindowsLaunchDriveCleanup(Record,backend,(_,_)=>Task.FromResult(true)).CleanupStoppedAsync();
        Assert.Empty(backend.Removed);Assert.Empty(backend.Targets);Assert.False(File.Exists(Record));
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
