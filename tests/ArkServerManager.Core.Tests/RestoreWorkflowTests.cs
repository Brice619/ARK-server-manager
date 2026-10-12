using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class RestoreWorkflowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-restore-flow-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task VerifiedRestoreStopsThenSwapsStateAndRetainsPreviousWorld()
    {
        var oldSnapshot = Path.Combine(root, "snapshot-source");
        var live = Path.Combine(root, "live");
        Directory.CreateDirectory(oldSnapshot);
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(oldSnapshot, "world.ark"), "restore point");
        await File.WriteAllTextAsync(Path.Combine(live, "world.ark"), "current world");
        await File.WriteAllTextAsync(Path.Combine(live, "extra.arkprofile"), "new player");
        var id = Guid.NewGuid();
        var backup = Path.Combine(root, "backup.zip");
        await new SnapshotArchive().CreateAsync(oldSnapshot, backup, id, ArkEdition.ASE, 1);
        var events = new List<string>();
        var service = new ServerRestoreService(_ => { events.Add("stop"); return Task.FromResult(true); },
            async _ => { events.Add("start"); Assert.Equal("restore point", await File.ReadAllTextAsync(Path.Combine(live, "world.ark"))); }, DirectScope);
        var result = await service.RestoreAsync(backup, live, id, ArkEdition.ASE, true, retainPreviousState: true);
        Assert.True(result.Restored);
        Assert.True(result.Restarted);
        Assert.False(File.Exists(Path.Combine(live, "extra.arkprofile")));
        Assert.Equal("current world", await File.ReadAllTextAsync(Path.Combine(result.PreviousStateDirectory!, "world.ark")));
        Assert.Equal(new[] { "stop", "start" }, events);
    }

    [Fact]
    public async Task FailedStopDoesNotReplaceStateOrLeaveStagingFiles()
    {
        var live = Path.Combine(root, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "world.ark"), "unchanged");
        var id = Guid.NewGuid();
        var backup = Path.Combine(root, "backup.zip");
        await new SnapshotArchive().CreateAsync(live, backup, id, ArkEdition.ASA, 1);
        var result = await new ServerRestoreService(_ => Task.FromResult(false), _ => throw new InvalidOperationException(), DirectScope)
            .RestoreAsync(backup, live, id, ArkEdition.ASA, true, retainPreviousState: true);
        Assert.False(result.Restored);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(Path.Combine(live, "world.ark")));
        Assert.Empty(Directory.GetDirectories(root, ".restore-*"));
    }

    [Fact]
    public async Task InvalidPathDoesNotLockOutLaterRestore()
    {
        var live = Path.Combine(root, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "world.ark"), "valid");
        var id = Guid.NewGuid();
        var backup = Path.Combine(root, "backup.zip");
        await new SnapshotArchive().CreateAsync(live, backup, id, ArkEdition.ASE, 1);
        var service = new ServerRestoreService(_ => Task.FromResult(true), _ => Task.CompletedTask, DirectScope);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RestoreAsync(backup, "bad\0path", id,
            ArkEdition.ASE, false, retainPreviousState: true));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await service.RestoreAsync(backup, live, id, ArkEdition.ASE, false,
            retainPreviousState: true, deadline.Token);
        Assert.True(result.Restored);
    }

    [Fact]
    public async Task MissingStoppedScopeAuthorizationLeavesCurrentWorldUnchanged()
    {
        var live = Path.Combine(root, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "world.ark"), "unchanged");
        var id = Guid.NewGuid();
        var backup = Path.Combine(root, "backup.zip");
        await new SnapshotArchive().CreateAsync(live, backup, id, ArkEdition.ASE, 1);
        var restarted = false;
        var service = new ServerRestoreService(_ => Task.FromResult(true),
            _ => { restarted = true; return Task.CompletedTask; }, (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(backup, live, id,
            ArkEdition.ASE, true, retainPreviousState: true));
        Assert.False(restarted);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(Path.Combine(live, "world.ark")));
        Assert.Empty(Directory.GetDirectories(root, ".restore-*"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task CleanupWaitsForSuccessfulStartup(bool restart, bool startupFails)
    {
        var live = Path.Combine(root, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "world.ark"), "previous");
        var id = Guid.NewGuid();
        var backup = Path.Combine(root, "backup.zip");
        await new SnapshotArchive().CreateAsync(live, backup, id, ArkEdition.ASE, 1);
        var service = new ServerRestoreService(_ => Task.FromResult(true), _ =>
        {
            Assert.Single(Directory.GetDirectories(root, ".restore-previous-*"));
            if (startupFails) throw new IOException("Not ready");
            return Task.CompletedTask;
        }, DirectScope);
        var result = await service.RestoreAsync(backup, live, id, ArkEdition.ASE, restart,
            retainPreviousState: false);
        Assert.True(result.Restored);
        if (restart && !startupFails)
        { Assert.Null(result.PreviousStateDirectory); Assert.Empty(Directory.GetDirectories(root, ".restore-previous-*")); }
        else
        { Assert.NotNull(result.PreviousStateDirectory); Assert.True(Directory.Exists(result.PreviousStateDirectory)); }
    }

    private static Task DirectScope(Func<CancellationToken, Task> change, CancellationToken ct) => change(ct);

    [Fact]
    public async Task WorldFolderRestorePreservesSeparateConfigurationAndQueuedSettings()
    {
        var saved = Path.Combine(root, "ShooterGame", "Saved");
        var world = Path.Combine(saved, "SavedArks");
        var config = Path.Combine(saved, "Config", "WindowsServer", "GameUserSettings.ini");
        var pending = Path.Combine(root, "app-settings", "pending-settings.json");
        Directory.CreateDirectory(world);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        await File.WriteAllTextAsync(Path.Combine(world, "world.ark"), "backup world");
        await File.WriteAllTextAsync(config, "[SessionSettings]\nSessionName=Current configuration\n");
        await File.WriteAllTextAsync(pending, "queued settings fixture");
        var id = Guid.NewGuid();
        var archive = Path.Combine(root, "backups", "hour-00000001.zip");
        await new SnapshotArchive().CreateAsync(world, archive, id, ArkEdition.ASE, 1);
        await File.WriteAllTextAsync(Path.Combine(world, "world.ark"), "current world");
        var service = new ServerRestoreService(_ => Task.FromResult(true), _ => Task.CompletedTask, DirectScope);
        var result = await service.RestoreAsync(archive, world, id, ArkEdition.ASE, false, false, expectedRuntimeHour: 1);
        Assert.True(result.Restored);
        Assert.Equal("backup world", await File.ReadAllTextAsync(Path.Combine(world, "world.ark")));
        Assert.Equal("[SessionSettings]\nSessionName=Current configuration\n", await File.ReadAllTextAsync(config));
        Assert.Equal("queued settings fixture", await File.ReadAllTextAsync(pending));
    }

    [Fact]
    public async Task WrongRuntimePointIsRejectedBeforeStoppingOrReplacingState()
    {
        var live = Path.Combine(root, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "world.ark"), "current world");
        var id = Guid.NewGuid();
        var backup = Path.Combine(root, "backup.zip");
        await new SnapshotArchive().CreateAsync(live, backup, id, ArkEdition.ASE, 2);
        var service = new ServerRestoreService(_ => throw new InvalidOperationException("Must not stop"),
            _ => throw new InvalidOperationException("Must not start"), DirectScope);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(backup, live, id,
            ArkEdition.ASE, true, false, expectedRuntimeHour: 1));
        Assert.Equal("current world", await File.ReadAllTextAsync(Path.Combine(live, "world.ark")));
        Assert.Empty(Directory.GetDirectories(root, ".restore-*"));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
