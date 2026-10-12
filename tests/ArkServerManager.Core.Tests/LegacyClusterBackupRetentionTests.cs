using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class LegacyClusterBackupRetentionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-legacy-retention-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task MeasuredRuntimeExpiresLegacyPositionsWithoutAgingStoppedTimeOrDoubleCounting()
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root, new(), Guid.NewGuid());
        var folder = Path.Combine(root, "backups"); Directory.CreateDirectory(folder);
        var history = new AutomaticBackupState(1, server.Id, server.Edition, TimeSpan.FromHours(10).Ticks, 1, null,
            Enumerable.Range(1, 10).Select(hour => new BackupHistoryEntry(hour, $"hour-{hour:D8}.zip", DateTimeOffset.UtcNow)).ToList(), "old failure")
            { QueuedHours = [2] };
        foreach (var entry in history.History) await File.WriteAllTextAsync(Path.Combine(folder, entry.FileName), "legacy");
        await File.WriteAllTextAsync(Path.Combine(folder, "untracked.zip"), "preserve");
        await new AtomicJsonStore<AutomaticBackupState>(Path.Combine(folder, "backup-history.json")).SaveAsync(history);
        var session = Guid.NewGuid();
        await LegacyClusterBackupRetention.TickAsync(server, new(session, TimeSpan.FromHours(1).Ticks));
        var state = await AutomaticBackupService.ReadRecordedAsync(server);
        Assert.Equal(9, state.History.Count); Assert.Equal(2, state.PendingHour); Assert.Empty(state.QueuedHours);
        Assert.Equal(history.RuntimeTicks, state.RuntimeTicks); Assert.False(File.Exists(Path.Combine(folder, "hour-00000001.zip")));
        await LegacyClusterBackupRetention.TickAsync(server, new(session, TimeSpan.FromHours(1).Ticks));
        await LegacyClusterBackupRetention.TickAsync(server, null);
        Assert.Equal(9, (await AutomaticBackupService.ReadRecordedAsync(server)).History.Count);
        await LegacyClusterBackupRetention.TickAsync(server, new(Guid.NewGuid(), TimeSpan.FromHours(9).Ticks));
        state = await AutomaticBackupService.ReadRecordedAsync(server);
        Assert.Empty(state.History); Assert.Null(state.PendingHour); Assert.Null(state.LastError);
        Assert.Equal(history.RuntimeTicks, state.RuntimeTicks);
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(folder, "untracked.zip")));
    }
    [Fact]
    public async Task FailedCleanupKeepsHistoryAndRetriesWithoutCountingRuntimeTwice()
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root, new(), Guid.NewGuid());
        var folder = Path.Combine(root, "backups"); Directory.CreateDirectory(folder);
        var historyPath = Path.Combine(folder, "backup-history.json");
        var clockPath = Path.Combine(folder, "legacy-retention.json");
        var archive = Path.Combine(folder, "hour-00000001.zip"); Directory.CreateDirectory(archive);
        var original = new AutomaticBackupState(1, server.Id, server.Edition, TimeSpan.FromHours(10).Ticks, null, null,
            [new(1, "hour-00000001.zip", DateTimeOffset.UtcNow)], null);
        await new AtomicJsonStore<AutomaticBackupState>(historyPath).SaveAsync(original);
        var observation = new RunningTimeObservation(Guid.NewGuid(), TimeSpan.FromHours(1).Ticks);
        await Assert.ThrowsAsync<IOException>(() => LegacyClusterBackupRetention.TickAsync(server, observation));
        Assert.Single((await AutomaticBackupService.ReadRecordedAsync(server)).History);
        Assert.True(Directory.Exists(archive));
        var checkpoint = await new AtomicJsonStore<LegacyBackupRetentionState>(clockPath).LoadAsync();
        Assert.NotNull(checkpoint); Assert.Equal(TimeSpan.FromHours(1).Ticks, checkpoint.AdditionalRuntimeTicks);
        Directory.Delete(archive); await File.WriteAllTextAsync(archive, "recorded legacy archive");
        await LegacyClusterBackupRetention.TickAsync(server, observation);
        Assert.False(File.Exists(archive)); Assert.Empty((await AutomaticBackupService.ReadRecordedAsync(server)).History);
        Assert.Equal(checkpoint, await new AtomicJsonStore<LegacyBackupRetentionState>(clockPath).LoadAsync());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
