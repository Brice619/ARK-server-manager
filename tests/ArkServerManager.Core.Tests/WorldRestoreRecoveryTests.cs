using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class WorldRestoreRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-world-recovery-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CleanupRetriesAfterFailureWithoutRemovingCurrentWorldOrSettings()
    {
        var (server, record, store) = await Record(WorldRestorePhase.Replaced);
        Directory.CreateDirectory(record.SavedDirectory);
        var world = Path.Combine(record.SavedDirectory, "current.ark");
        var settings = Path.Combine(record.SavedDirectory, "Game.ini");
        await File.WriteAllTextAsync(world, "restored world");
        await File.WriteAllTextAsync(settings, "current settings");
        var retained = Path.Combine(record.PreviousDirectory, "world.ark");
        using (var locked = new FileStream(retained, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            if (OperatingSystem.IsWindows())
            {
                await Assert.ThrowsAnyAsync<IOException>(() => WorldRestoreRecovery.CleanupAfterVerifiedStartAsync(server));
                Assert.Equal(WorldRestorePhase.Started, (await store.LoadAsync())!.Phase);
                Assert.True(Directory.Exists(record.PreviousDirectory));
            }
        }
        await WorldRestoreRecovery.CleanupAfterVerifiedStartAsync(server);
        await WorldRestoreRecovery.CleanupAfterVerifiedStartAsync(server);
        Assert.Equal(WorldRestorePhase.Cleaned, (await store.LoadAsync())!.Phase);
        Assert.False(Directory.Exists(record.PreviousDirectory));
        Assert.Equal("restored world", await File.ReadAllTextAsync(world));
        Assert.Equal("current settings", await File.ReadAllTextAsync(settings));
    }

    [Fact]
    public async Task InterruptedReplacementBlocksStartupAndCleanupWithoutRemovingPreviousState()
    {
        var (server, record, store) = await Record(WorldRestorePhase.Replacing);
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorldRestoreRecovery.RequireConsistentAsync(server));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorldRestoreRecovery.CleanupAfterVerifiedStartAsync(server));
        Assert.Equal("retained", await File.ReadAllTextAsync(Path.Combine(record.PreviousDirectory, "world.ark")));
        Assert.Equal(WorldRestorePhase.Replacing, (await store.LoadAsync())!.Phase);
    }

    [Fact]
    public async Task ForgedRecordedPathIsRejectedBeforeCleanup()
    {
        var (server, record, store) = await Record(WorldRestorePhase.Replaced);
        var unrelated = Path.Combine(root, "unrelated"); Directory.CreateDirectory(unrelated);
        await File.WriteAllTextAsync(Path.Combine(unrelated, "keep.txt"), "keep");
        await store.SaveAsync(record with { PreviousDirectory = unrelated });
        await Assert.ThrowsAsync<InvalidDataException>(() => WorldRestoreRecovery.CleanupAfterVerifiedStartAsync(server));
        Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
        Assert.True(Directory.Exists(record.PreviousDirectory));
    }

    private async Task<(ServerDefinition, WorldRestoreRecord, AtomicJsonStore<WorldRestoreRecord>)> Record(WorldRestorePhase phase)
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "server"), new() { Name = "Server" });
        var id = Guid.NewGuid();
        var record = new WorldRestoreRecord(id, server.Id, server.Edition, 1, Path.Combine(server.Directory, "ShooterGame", "Saved"),
            Path.Combine(server.Directory, ".world-restore-stage-" + id.ToString("N")),
            Path.Combine(server.Directory, ".world-restore-previous-" + id.ToString("N")), phase);
        Directory.CreateDirectory(record.PreviousDirectory);
        await File.WriteAllTextAsync(Path.Combine(record.PreviousDirectory, "world.ark"), "retained");
        var store = new AtomicJsonStore<WorldRestoreRecord>(Path.Combine(server.Directory, "backups", "restore-transactions", id.ToString("N") + ".json"));
        await store.SaveAsync(record); return (server, record, store);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
