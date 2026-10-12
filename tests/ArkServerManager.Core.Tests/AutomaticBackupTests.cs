using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class AutomaticBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-auto-backup-test-" + Guid.NewGuid().ToString("N"));
    private sealed class StableState(string directory) : IStableServerState
    {
        public string Directory => directory;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private ServerDefinition Server(Guid id) => new(id, ArkEdition.ASE, "TheIsland", Path.Combine(root, "server"), new() { Name = "Server" });
    private async Task<string> StateDirectory()
    {
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "world.ark"), "saved world");
        return state;
    }

    [Fact]
    public async Task HistoryQueryDoesNotCreateBackupFolders()
    {
        var server = Server(Guid.NewGuid());
        var history = await AutomaticBackupService.ReadRecordedAsync(server);
        Assert.Equal(server.Id, history.ServerId);
        Assert.Empty(history.History);
        Assert.False(Directory.Exists(server.Directory));
    }

    [Fact]
    public async Task HistoryQueryPreservesFilesAndRejectsAnotherServersHistory()
    {
        var server = Server(Guid.NewGuid());
        var folder = Path.Combine(server.Directory, "backups");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "hour-00000001.zip");
        await File.WriteAllTextAsync(file, "not an archive; read history must not verify or expire it");
        var state = new AutomaticBackupState(1, server.Id, server.Edition, TimeSpan.FromHours(20).Ticks,
            null, null, [new(1, "hour-00000001.zip", DateTimeOffset.UtcNow)], null);
        var store = new AtomicJsonStore<AutomaticBackupState>(Path.Combine(folder, "backup-history.json"));
        await store.SaveAsync(state);
        var original = await File.ReadAllBytesAsync(Path.Combine(folder, "backup-history.json"));
        Assert.Single((await AutomaticBackupService.ReadRecordedAsync(server)).History);
        Assert.True(File.Exists(file));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(folder, "backup-history.json")));
        await Assert.ThrowsAsync<InvalidDataException>(() => AutomaticBackupService.ReadRecordedAsync(server with { Id = Guid.NewGuid() }));
    }

    [Fact]
    public async Task CreatesHourlyBackupsAndExpiresByRuntimeWhileStoppedTimeDoesNotCount()
    {
        var source = await StateDirectory();
        var server = Server(Guid.NewGuid());
        var service = new AutomaticBackupService(server, TimeSpan.FromMinutes(1), _ => Task.FromResult<IStableServerState>(new StableState(source)), _ => Task.CompletedTask);
        var now = DateTimeOffset.UtcNow;
        for (var hour = 1; hour <= 11; hour++) await service.TickAsync(TimeSpan.FromHours(1), true, now.AddHours(hour));
        var state = await service.ReadAsync();
        Assert.Equal(10, state.History.Count);
        Assert.DoesNotContain(state.History, h => h.RuntimeHour == 1);
        Assert.False(File.Exists(Path.Combine(server.Directory, "backups", "hour-00000001.zip")));
        Assert.Equal(10, Directory.GetFiles(Path.Combine(server.Directory, "backups"), "*.zip").Length);
        await service.TickAsync(TimeSpan.FromDays(5), false, now.AddDays(5));
        Assert.Equal(state.RuntimeTicks, (await service.ReadAsync()).RuntimeTicks);
    }

    [Fact]
    public async Task FailedWorkSurvivesReloadAndRetriesWithoutStoppingServer()
    {
        var source = await StateDirectory();
        var server = Server(Guid.NewGuid());
        var messages = new List<string>();
        var now = DateTimeOffset.UtcNow;
        var failing = new AutomaticBackupService(server, TimeSpan.FromMinutes(1), _ => throw new IOException("Disk unavailable"),
            message => { messages.Add(message); return Task.CompletedTask; });
        var failed = await failing.TickAsync(TimeSpan.FromHours(1), true, now);
        Assert.Equal(1, failed.PendingHour);
        Assert.Empty(failed.History);
        Assert.Single(messages);
        var calls = 0;
        var recovering = new AutomaticBackupService(server, TimeSpan.FromMinutes(1), _ =>
        { calls++; return Task.FromResult<IStableServerState>(new StableState(source)); }, _ => Task.CompletedTask);
        await recovering.TickAsync(TimeSpan.Zero, true, now.AddSeconds(30));
        Assert.Equal(0, calls);
        var success = await recovering.TickAsync(TimeSpan.Zero, true, now.AddMinutes(1));
        Assert.Null(success.PendingHour);
        Assert.Null(success.LastError);
        Assert.Single(success.History);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NewHourlyDeadlineDoesNotEraseEarlierRetryWork()
    {
        var source = await StateDirectory();
        var server = Server(Guid.NewGuid());
        var fail = true;
        var service = new AutomaticBackupService(server, TimeSpan.FromSeconds(1), _ => fail
            ? throw new IOException("Failed") : Task.FromResult<IStableServerState>(new StableState(source)), _ => Task.CompletedTask);
        var now = DateTimeOffset.UtcNow;
        await service.TickAsync(TimeSpan.FromHours(1), true, now);
        var queued = await service.TickAsync(TimeSpan.FromHours(1), true, now.AddHours(1));
        Assert.Equal(1, queued.PendingHour);
        Assert.Equal(2, Assert.Single(queued.QueuedHours));
        fail = false;
        var first = await service.TickAsync(TimeSpan.Zero, true, now.AddHours(1).AddSeconds(1));
        Assert.Equal(2, first.PendingHour);
        var second = await service.TickAsync(TimeSpan.Zero, true, now.AddHours(1).AddSeconds(2));
        Assert.Null(second.PendingHour);
        Assert.Equal(new long[] { 1, 2 }, second.History.Select(h => h.RuntimeHour));
    }

    [Fact]
    public async Task OldestBackupExpiresEvenIfItsReplacementFails()
    {
        var source = await StateDirectory();
        var server = Server(Guid.NewGuid());
        var fail = false;
        var service = new AutomaticBackupService(server, TimeSpan.FromSeconds(1), _ => fail
            ? throw new IOException("Backup failed") : Task.FromResult<IStableServerState>(new StableState(source)), _ => Task.CompletedTask);
        var now = DateTimeOffset.UtcNow;
        for (var i = 1; i <= 10; i++) await service.TickAsync(TimeSpan.FromHours(1), true, now.AddHours(i));
        fail = true;
        var state = await service.TickAsync(TimeSpan.FromHours(1), true, now.AddHours(11));
        Assert.Equal(9, state.History.Count);
        Assert.Equal(11, state.PendingHour);
        Assert.False(File.Exists(Path.Combine(server.Directory, "backups", "hour-00000001.zip")));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("unavailable")]
    [InlineData("provider-canceled")]
    public async Task SaveCoordinationFailureRemainsScheduledForRetry(string failure)
    {
        var server = Server(Guid.NewGuid());
        var source = await StateDirectory();
        var attempts = 0;
        var messages = new List<string>();
        var service = new AutomaticBackupService(server, TimeSpan.FromMinutes(1), _ =>
        {
            if (++attempts > 1) return Task.FromResult<IStableServerState>(new StableState(source));
            throw failure switch
            {
                "timeout" => new TimeoutException("Save acknowledgement timed out"),
                "unavailable" => new InvalidOperationException("Server changed state during save coordination"),
                _ => new OperationCanceledException("Provider deadline expired")
            };
        }, message => { messages.Add(message); return Task.CompletedTask; });
        var now = DateTimeOffset.UtcNow;
        var failed = await service.TickAsync(TimeSpan.FromHours(1), true, now);
        Assert.Equal(1, failed.PendingHour);
        Assert.Equal(now.AddMinutes(1), failed.RetryAt);
        Assert.Empty(failed.History);
        Assert.Single(messages);
        await service.TickAsync(TimeSpan.Zero, true, now.AddSeconds(30));
        Assert.Equal(1, attempts);
        var recovered = await service.TickAsync(TimeSpan.Zero, true, now.AddMinutes(1));
        Assert.Null(recovered.PendingHour);
        Assert.Null(recovered.LastError);
        Assert.Single(recovered.History);
    }

    [Fact]
    public async Task ManagerCancellationPropagatesWithoutReportingBackupFailure()
    {
        var server = Server(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var messages = new List<string>();
        var service = new AutomaticBackupService(server, TimeSpan.FromMinutes(1), _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }, message => { messages.Add(message); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TickAsync(
            TimeSpan.FromHours(1), true, DateTimeOffset.UtcNow, cancellation.Token));
        var recorded = await service.ReadAsync();
        Assert.Equal(1, recorded.PendingHour);
        Assert.Null(recorded.LastError);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task MeasuredRuntimeRetriesAndReloadDoNotDoubleCountElapsedTime()
    {
        var source = await StateDirectory(); var server = Server(Guid.NewGuid());
        var session = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var first = new AutomaticBackupService(server, TimeSpan.FromMinutes(1),
            _ => throw new TimeoutException("Save timed out"), _ => Task.CompletedTask);
        var reading = new RunningTimeObservation(session, TimeSpan.FromHours(1).Ticks);
        await first.TickMeasuredAsync(reading, now);
        var reloaded = new AutomaticBackupService(server, TimeSpan.FromMinutes(1),
            _ => Task.FromResult<IStableServerState>(new StableState(source)), _ => Task.CompletedTask);
        var result = await reloaded.TickMeasuredAsync(reading, now.AddMinutes(1));
        Assert.Equal(TimeSpan.FromHours(1).Ticks, result.RuntimeTicks);
        Assert.Single(result.History); Assert.Null(result.PendingHour);
        var advanced = await reloaded.TickMeasuredAsync(new(session, TimeSpan.FromMinutes(90).Ticks), now.AddMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(90).Ticks, advanced.RuntimeTicks);
        var nextHost = await reloaded.TickMeasuredAsync(new(Guid.NewGuid(), TimeSpan.FromMinutes(30).Ticks), now.AddDays(1));
        Assert.Equal(TimeSpan.FromHours(2).Ticks, nextHost.RuntimeTicks);
        Assert.Equal(2, nextHost.History.Count);
    }

    [Fact]
    public async Task RejectsBackwardsMeasuredRuntimeWithoutChangingCheckpoint()
    {
        var server = Server(Guid.NewGuid()); var session = Guid.NewGuid();
        var service = new AutomaticBackupService(server, TimeSpan.FromMinutes(1),
            _ => throw new IOException("Not due"), _ => Task.CompletedTask);
        var original = await service.TickMeasuredAsync(new(session, TimeSpan.FromMinutes(30).Ticks), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.TickMeasuredAsync(
            new(session, TimeSpan.FromMinutes(20).Ticks), DateTimeOffset.UtcNow));
        var unchanged = await service.ReadAsync();
        Assert.Equal(original.RuntimeTicks, unchanged.RuntimeTicks);
        Assert.Equal(original.LastRuntimeObservation, unchanged.LastRuntimeObservation);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
