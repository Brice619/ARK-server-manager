using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class CoordinatedBackupScheduleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-coordinated-schedule-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task FailedPointSurvivesNewHourAndRestartsWithoutDoubleCountingStoppedTime()
    {
        var id = Guid.NewGuid(); var map = Guid.NewGuid();
        var catalog = new CatalogDocument(1, [new(id, "Cluster", ArkEdition.ASE, new())], []);
        var session = Guid.NewGuid(); var now = DateTimeOffset.UtcNow; var fail = true;
        var published = new List<long>();
        Task<CoordinatedRestorePoint> Publish(long hour, CancellationToken ct)
        {
            if (fail) throw new IOException("Snapshot unavailable");
            published.Add(hour);
            return Task.FromResult(new CoordinatedRestorePoint(1, id, ArkEdition.ASE, hour,
                new(Guid.NewGuid(), now), [map]));
        }
        CoordinatedBackupService Service() => new(catalog, id, root, Publish, _ => Task.CompletedTask);
        var state = await Service().TickAsync(new(session, TimeSpan.FromHours(1).Ticks), now);
        Assert.Equal(new long[] { 1 }, state.PendingHours); Assert.NotNull(state.LastError);
        state = await Service().TickAsync(new(session, TimeSpan.FromHours(2).Ticks), now.AddHours(1));
        Assert.Equal(new long[] { 1, 2 }, state.PendingHours);
        fail = false;
        state = await Service().TickAsync(new(session, TimeSpan.FromHours(2).Ticks), now.AddDays(3));
        Assert.Equal(new long[] { 2 }, state.PendingHours); Assert.Null(state.LastError);
        state = await Service().TickAsync(new(Guid.NewGuid(), 0), now.AddDays(4));
        Assert.Empty(state.PendingHours); Assert.Equal(new long[] { 1, 2 }, published);
        Assert.Equal(TimeSpan.FromHours(2).Ticks, state.RuntimeTicks); Assert.Equal(2, state.History.Count);
    }

    [Fact]
    public async Task ManagerCancellationLeavesCheckpointPendingForLaterPublication()
    {
        var id = Guid.NewGuid(); var catalog = new CatalogDocument(1, [new(id, "Cluster", ArkEdition.ASE, new())], []);
        var session = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        using var cancellation = new CancellationTokenSource();
        var service = new CoordinatedBackupService(catalog, id, root,
            (_, ct) => { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException(); },
            _ => throw new InvalidOperationException("Manager cancellation must not report a failure"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TickAsync(new(session, TimeSpan.FromHours(1).Ticks), now, cancellation.Token));
        var restored = await new CoordinatedBackupService(catalog, id, root,
            (hour, _) => Task.FromResult(new CoordinatedRestorePoint(1, id, ArkEdition.ASE, hour,
                new(Guid.NewGuid(), now), [Guid.NewGuid()])), _ => Task.CompletedTask)
            .TickAsync(new(session, TimeSpan.FromHours(1).Ticks), now.AddMinutes(1));
        Assert.Empty(restored.PendingHours); Assert.Single(restored.History);
        Assert.Equal(TimeSpan.FromHours(1).Ticks, restored.RuntimeTicks);
    }
    [Fact]
    public async Task RetentionFailureKeepsItsRecordAndRetriesWithoutAgingStoppedTime()
    {
        var id = Guid.NewGuid(); var map = Guid.NewGuid(); var session = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var catalog = new CatalogDocument(1, [new(id, "Cluster", ArkEdition.ASE, new())], []);
        var fail = false; var expired = new List<long>();
        var service = new CoordinatedBackupService(catalog, id, root,
            (hour, _) => Task.FromResult(new CoordinatedRestorePoint(1, id, ArkEdition.ASE, hour,
                new(Guid.NewGuid(), now), [map])), _ => Task.CompletedTask,
            (hour, _) => { if (fail) throw new IOException("Cleanup unavailable"); expired.Add(hour); return Task.CompletedTask; });
        for (var hour = 1; hour <= 10; hour++) await service.TickAsync(new(session, TimeSpan.FromHours(hour).Ticks), now.AddHours(hour));
        fail = true;
        var state = await service.TickAsync(new(session, TimeSpan.FromHours(11).Ticks), now.AddHours(11));
        Assert.Equal(10, state.History.Count); Assert.Contains(state.History, p => p.RuntimeHour == 1);
        Assert.Equal(new long[] { 11 }, state.PendingHours); Assert.NotNull(state.LastError);
        fail = false;
        state = await service.TickAsync(new(session, TimeSpan.FromHours(11).Ticks), now.AddDays(3));
        Assert.Equal(10, state.History.Count); Assert.DoesNotContain(state.History, p => p.RuntimeHour == 1);
        Assert.Contains(state.History, p => p.RuntimeHour == 11); Assert.Null(state.LastError);
        Assert.Equal(new long[] { 1 }, expired); Assert.Equal(TimeSpan.FromHours(11).Ticks, state.RuntimeTicks);
    }

    [Fact]
    public async Task LinkedScheduleFolderRefusesBeforeWritingOrPublishing()
    {
        var id = Guid.NewGuid();
        var catalog = new CatalogDocument(1, [new(id, "Cluster", ArkEdition.ASE, new())], []);
        var storage = ManagedClusterStorage.Prepare(catalog, id, root);
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        var linked = Path.Combine(storage.Backups, "Coordinated");
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{linked}\" \"{outside}\"", UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var process = System.Diagnostics.Process.Start(start)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(deadline.Token); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(linked, outside);
        try
        {
            var publications = 0;
            var service = new CoordinatedBackupService(catalog, id, root,
                (_, _) => { publications++; throw new InvalidOperationException("Must not publish"); }, _ => Task.CompletedTask);
            await Assert.ThrowsAsync<IOException>(() => service.TickAsync(new(Guid.NewGuid(), TimeSpan.FromHours(1).Ticks), DateTimeOffset.UtcNow));
            Assert.Equal(0, publications); Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { Directory.Delete(linked); }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
