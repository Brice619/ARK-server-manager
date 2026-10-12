using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class InterruptedWorldRollbackTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-world-rollback-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InterruptedReplacementReturnsOriginalWorldAndOriginallyAbsentPlayerFolder(int step)
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root, new()); var operation = Guid.NewGuid();
        var saved = Path.Combine(root, "ShooterGame", "Saved");
        var stage = Path.Combine(root, ".world-restore-stage-" + operation.ToString("N"));
        var previous = Path.Combine(root, ".world-restore-previous-" + operation.ToString("N"));
        await Write(saved, "SavedArks/world.ark", "original world");
        await Write(saved, "Config/Game.ini", "current settings");
        await Write(stage, "SavedArks/world.ark", "restored world");
        await Write(stage, "SaveGames/player", "restored player");
        Directory.CreateDirectory(previous);
        if (step >= 1) Directory.Move(Path.Combine(saved, "SavedArks"), Path.Combine(previous, "SavedArks"));
        if (step >= 2)
        {
            Directory.Move(Path.Combine(stage, "SavedArks"), Path.Combine(saved, "SavedArks"));
            Directory.Move(Path.Combine(stage, "SaveGames"), Path.Combine(saved, "SaveGames"));
        }
        if (step == 3) Directory.Move(Path.Combine(saved, "SavedArks"), Path.Combine(stage, "SavedArks"));
        var record = new WorldRestoreRecord(operation, server.Id, server.Edition, 1, saved, stage, previous,
            step == 3 ? WorldRestorePhase.RollingBack : WorldRestorePhase.Replacing) { OriginalWorldFolders = ["SavedArks"] };
        await new AtomicJsonStore<WorldRestoreRecord>(Path.Combine(root, "backups", "restore-transactions", operation.ToString("N") + ".json")).SaveAsync(record);
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorldRestoreRecovery.RequireConsistentAsync(server));
        await WorldRestoreRecovery.RollbackInterruptedAsync(server, operation);
        Assert.Equal("original world", await File.ReadAllTextAsync(Path.Combine(saved, "SavedArks/world.ark")));
        Assert.False(Directory.Exists(Path.Combine(saved, "SaveGames")));
        Assert.Equal("current settings", await File.ReadAllTextAsync(Path.Combine(saved, "Config/Game.ini")));
        Assert.False(Directory.Exists(stage)); Assert.False(Directory.Exists(previous));
        Assert.Equal(WorldRestorePhase.RolledBack, Assert.Single(await WorldRestoreRecovery.ReadAsync(server)).Phase);
        await WorldRestoreRecovery.RequireConsistentAsync(server);
    }
    [Fact]
    public async Task CompletedRollbackCleanupDoesNotRollbackNewWorldOrPlayerChanges()
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root, new()); var operation = Guid.NewGuid();
        var saved = Path.Combine(root, "ShooterGame", "Saved");
        var stage = Path.Combine(root, ".world-restore-stage-" + operation.ToString("N"));
        var previous = Path.Combine(root, ".world-restore-previous-" + operation.ToString("N"));
        await Write(saved, "SavedArks/world.ark", "world after recovered startup");
        await Write(saved, "SaveGames/new-player", "new player after recovery");
        await Write(stage, "SaveGames/old-staged-player", "discarded restored state");
        Directory.CreateDirectory(previous);
        var record = new WorldRestoreRecord(operation, server.Id, server.Edition, 1, saved, stage, previous, WorldRestorePhase.RolledBack)
            { OriginalWorldFolders = ["SavedArks"] };
        await new AtomicJsonStore<WorldRestoreRecord>(Path.Combine(root, "backups", "restore-transactions", operation.ToString("N") + ".json")).SaveAsync(record);
        await WorldRestoreRecovery.RollbackInterruptedAsync(server, operation);
        await WorldRestoreRecovery.RollbackInterruptedAsync(server, operation);
        Assert.Equal("world after recovered startup", await File.ReadAllTextAsync(Path.Combine(saved, "SavedArks/world.ark")));
        Assert.Equal("new player after recovery", await File.ReadAllTextAsync(Path.Combine(saved, "SaveGames/new-player")));
        Assert.False(Directory.Exists(stage)); Assert.False(Directory.Exists(previous));
    }

    [Fact]
    public async Task InconsistentSecondFolderRefusesBeforeFirstFolderMutation()
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root, new()); var operation = Guid.NewGuid();
        var saved = Path.Combine(root, "ShooterGame", "Saved");
        var stage = Path.Combine(root, ".world-restore-stage-" + operation.ToString("N"));
        var previous = Path.Combine(root, ".world-restore-previous-" + operation.ToString("N"));
        await Write(previous, "SavedArks/world.ark", "original world");
        await Write(stage, "SavedArks/world.ark", "staged world");
        await Write(previous, "SaveGames/player", "unexpected original player");
        var record = new WorldRestoreRecord(operation, server.Id, server.Edition, 1, saved, stage, previous, WorldRestorePhase.Replacing)
            { OriginalWorldFolders = ["SavedArks"] };
        await new AtomicJsonStore<WorldRestoreRecord>(Path.Combine(root, "backups", "restore-transactions", operation.ToString("N") + ".json")).SaveAsync(record);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorldRestoreRecovery.RollbackInterruptedAsync(server, operation));
        Assert.False(Directory.Exists(saved));
        Assert.Equal("original world", await File.ReadAllTextAsync(Path.Combine(previous, "SavedArks/world.ark")));
        Assert.Equal(WorldRestorePhase.Replacing, Assert.Single(await WorldRestoreRecovery.ReadAsync(server)).Phase);
    }

    private static async Task Write(string folder, string relative, string value)
    {
        var path = Path.Combine(folder, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, value);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
