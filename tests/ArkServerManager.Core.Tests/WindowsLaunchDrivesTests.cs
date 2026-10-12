using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class WindowsLaunchDrivesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-launch-drives-" + Guid.NewGuid().ToString("N"));
    private string Record => Path.Combine(root, "drives.json");
    private sealed class Backend : IWindowsLaunchDriveBackend
    {
        public Dictionary<char, List<string>> Targets = [];
        public bool FailCreate; public int Creates; public List<char> Removed = [];
        public IReadOnlyList<string> Query(char letter) => Targets.GetValueOrDefault(letter) ?? [];
        public void Create(char letter, string target)
        {
            Creates++; if (FailCreate) throw new IOException("create failure");
            Assert.Empty(Query(letter)); Targets[letter] = [target];
        }
        public void RemoveExact(char letter, string target)
        { Assert.Equal([target], Query(letter)); Targets.Remove(letter); Removed.Add(letter); }
        public string Resolve(string path) => path.Length == 3 && path[1] == ':' ? Query(path[0]).Single()[4..] : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    [Fact]
    public async Task AllocationPreservesOccupiedDriveAndSurvivesManagerReload()
    {
        var backend = new Backend(); backend.Targets['Z'] = ["foreign target"];
        var stopped = true;
        WindowsLaunchDrives Manager() => new(Record, backend, (_, _) => Task.FromResult(stopped));
        Assert.Equal("Y:\\", await Manager().GetOrCreateAsync(root));
        Assert.Equal("Y:\\", await Manager().GetOrCreateAsync(root));
        Assert.Equal(1, backend.Creates);
        stopped = false; await Manager().CleanupStoppedAsync(); Assert.Empty(backend.Removed);
        stopped = true; await Manager().CleanupStoppedAsync(); Assert.Equal(['Y'], backend.Removed);
        Assert.Equal(["foreign target"], backend.Query('Z'));
        Assert.Empty((await new AtomicJsonStore<WindowsLaunchDriveDocument>(Record).LoadAsync())!.Drives);
    }

    [Fact]
    public async Task ChangedOrStackedOwnedTargetIsNeverRemoved()
    {
        var backend = new Backend(); var manager = new WindowsLaunchDrives(Record, backend, (_, _) => Task.FromResult(true));
        await manager.GetOrCreateAsync(root);
        var owned = backend.Targets['Z'].Single();
        backend.Targets['Z'] = ["foreign target", owned];
        await manager.CleanupStoppedAsync(); Assert.Empty(backend.Removed);
        Assert.Equal("Y:\\", await manager.GetOrCreateAsync(root));
        backend.Targets['Z'] = ["foreign target"];
        await manager.CleanupStoppedAsync(); Assert.Equal(['Y'], backend.Removed);
        Assert.Equal(["foreign target"], backend.Query('Z'));
    }

    [Fact]
    public async Task UncertainPreparedMappingIsPreservedAndAnotherLetterIsUsed()
    {
        var backend = new Backend(); var target = "\\??\\" + root;
        backend.Targets['Z'] = [target];
        await new AtomicJsonStore<WindowsLaunchDriveDocument>(Record).SaveAsync(new(1,
            [new(Guid.NewGuid(), 'Z', root, target, LaunchDrivePhase.Prepared)]));
        var manager = new WindowsLaunchDrives(Record, backend, (_, _) => Task.FromResult(true));
        Assert.Equal("Y:\\", await manager.GetOrCreateAsync(root));
        await manager.CleanupStoppedAsync(); Assert.Equal(['Y'], backend.Removed);
        Assert.Equal([target], backend.Query('Z'));
        Assert.Equal(LaunchDrivePhase.Prepared, (await new AtomicJsonStore<WindowsLaunchDriveDocument>(Record).LoadAsync())!.Drives.Single().Phase);
    }

    [Fact]
    public async Task FailedCreationAndRebootAbsentMappingsCanBeRecoveredWithoutConsumingLetters()
    {
        var backend = new Backend { FailCreate = true };
        WindowsLaunchDrives Manager() => new(Record, backend, (_, _) => Task.FromResult(true));
        await Assert.ThrowsAsync<IOException>(() => Manager().GetOrCreateAsync(root));
        Assert.Equal(LaunchDrivePhase.Prepared, (await new AtomicJsonStore<WindowsLaunchDriveDocument>(Record).LoadAsync())!.Drives.Single().Phase);
        backend.FailCreate = false;
        Assert.Equal("Z:\\", await Manager().GetOrCreateAsync(root));
        backend.Targets.Clear(); // Virtual mapping absent after OS restart.
        Assert.Equal("Z:\\", await Manager().GetOrCreateAsync(root));
        Assert.Single((await new AtomicJsonStore<WindowsLaunchDriveDocument>(Record).LoadAsync())!.Drives);
    }

    [Fact]
    public async Task UnverifiedInstallationCannotCreateAMapping()
    {
        var backend = new Backend();
        var manager = new WindowsLaunchDrives(Record, backend, (_, _) => Task.FromResult(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetOrCreateAsync(root));
        Assert.Equal(0, backend.Creates); Assert.False(File.Exists(Record));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
