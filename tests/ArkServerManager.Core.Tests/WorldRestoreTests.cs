using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class WorldRestoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-world-restore-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RestoresBothWorldFoldersAndPreservesSettings(bool restart, bool failStartup)
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "server"), new() { Name = "Server" });
        var saved = Path.Combine(server.Directory, "ShooterGame", "Saved");
        var snapshot = Path.Combine(root, "snapshot");
        await Write(snapshot, "SavedArks/map/world.ark", "saved world");
        await Write(snapshot, "SaveGames/player.arkprofile", "saved player");
        var archive = Path.Combine(root, "backup.zip");
        await new SnapshotArchive().CreateAsync(snapshot, archive, server.Id, server.Edition, 1, layout: SnapshotLayout.WorldSavedFolders);
        await Write(saved, "SavedArks/map/world.ark", "current world");
        await Write(saved, "SaveGames/new.arkprofile", "new player");
        await Write(saved, "Config/WindowsServer/Game.ini", "current config");
        await Write(server.Directory, "app-settings/pending.json", "queued settings");
        var stops = 0; var starts = 0;
        var service = new WorldRestoreService(_ => { stops++; return Task.FromResult(true); }, _ =>
        {
            starts++;
            Assert.Single(Directory.GetDirectories(server.Directory, ".world-restore-previous-*"));
            if (failStartup) throw new IOException("Not ready");
            return Task.CompletedTask;
        }, (change, ct) => change(ct));
        var operationId = Guid.NewGuid();
        var result = await service.RestoreAsync(server, archive, 1, operationId, restart);
        Assert.True(result.Restored); Assert.Equal(restart && !failStartup, result.Restarted);
        Assert.Equal(1, stops); Assert.Equal(restart ? 1 : 0, starts);
        Assert.Equal("saved world", await File.ReadAllTextAsync(Path.Combine(saved, "SavedArks/map/world.ark")));
        Assert.Equal("saved player", await File.ReadAllTextAsync(Path.Combine(saved, "SaveGames/player.arkprofile")));
        Assert.False(File.Exists(Path.Combine(saved, "SaveGames/new.arkprofile")));
        Assert.Equal("current config", await File.ReadAllTextAsync(Path.Combine(saved, "Config/WindowsServer/Game.ini")));
        Assert.Equal("queued settings", await File.ReadAllTextAsync(Path.Combine(server.Directory, "app-settings/pending.json")));
        if (result.Restarted) Assert.Null(result.PreviousStateDirectory);
        else Assert.Equal("current world", await File.ReadAllTextAsync(Path.Combine(result.PreviousStateDirectory!, "SavedArks/map/world.ark")));
        var journal = await new AtomicJsonStore<WorldRestoreRecord>(Path.Combine(server.Directory, "backups", "restore-transactions", operationId.ToString("N") + ".json")).LoadAsync();
        Assert.Equal(result.Restarted ? WorldRestorePhase.Cleaned : WorldRestorePhase.Replaced, journal!.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(server, archive, 1, operationId, false));
    }

    [Fact]
    public async Task CoordinatedCaptureMismatchRefusesBeforeStoppingOrReplacingWorld()
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "server"), new() { Name = "Server" });
        var saved = Path.Combine(server.Directory, "ShooterGame", "Saved");
        var snapshot = Path.Combine(root, "snapshot");
        await Write(snapshot, "SavedArks/world.ark", "saved world");
        await Write(saved, "SavedArks/world.ark", "current world");
        var archive = Path.Combine(root, "backup.zip");
        await new SnapshotArchive().CreateAsync(snapshot, archive, server.Id, server.Edition, 1,
            layout: SnapshotLayout.WorldSavedFolders, capture: new(Guid.NewGuid(), DateTimeOffset.UtcNow));
        var stops = 0;
        var service = new WorldRestoreService(_ => { stops++; return Task.FromResult(true); },
            _ => throw new InvalidOperationException("Must not start"), (_, _) => throw new InvalidOperationException("Must not replace"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(server, archive, 1, Guid.NewGuid(), false,
            expectedCapture: new(Guid.NewGuid(), DateTimeOffset.UtcNow)));
        Assert.Equal(0, stops); Assert.Equal("current world", await File.ReadAllTextAsync(Path.Combine(saved, "SavedArks/world.ark")));
        Assert.Empty(Directory.GetDirectories(server.Directory, ".world-restore-stage-*"));
    }

    private static async Task Write(string folder, string relative, string text)
    { var path = Path.Combine(folder, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, text); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
