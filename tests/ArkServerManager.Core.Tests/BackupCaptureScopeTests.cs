using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class BackupCaptureScopeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-backup-scope-" + Guid.NewGuid().ToString("N"));
    private sealed class Runtime : IServerRuntime
    {
        public ServerState State { get; private set; }
        public Task StartAsync(ServerDefinition _, CancellationToken ct) { State = ServerState.Running; return Task.CompletedTask; }
        public Task<bool> StopAsync(CancellationToken ct) { State = ServerState.Stopped; return Task.FromResult(true); }
    }
    private sealed class Captured(string source, string directory) : IStableServerState
    {
        public string Directory => directory;
        public string SourceDirectory => source;
        public bool Disposed;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private async Task<(ServerDefinition Server, ServerLifecycleCoordinator Coordinator)> SetupAsync()
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "server"),
            new() { Name = "Map", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        await repository.ChangeAsync(c => c.AddServer(server));
        Directory.CreateDirectory(server.Directory);
        return (server, new(repository, _ => new Runtime(), (_, _) => Task.CompletedTask));
    }
    private static Task<InstallationStopCheck> Stopped(ServerDefinition server, CancellationToken ct)
        => Task.FromResult(new InstallationStopCheck(server.Directory, InstallationStopState.Stopped, [], [], DateTimeOffset.UtcNow));

    [Fact]
    public async Task RunningCaptureSavesBeforeSnapshotAndExcludesStopUntilLeaseReleased()
    {
        var (server, coordinator) = await SetupAsync();
        await coordinator.ExecuteMemberAsync(server.Id, LifecycleAction.Start);
        var events = new List<string>(); Captured? captured = null;
        var lease = await coordinator.AcquireBackupStateAsync(server,
            (_, _) => { events.Add("save"); return Task.CompletedTask; },
            (source, _) => { events.Add("snapshot"); captured = new(source, Path.Combine(root, "immutable")); return Task.FromResult<IStableServerState>(captured); }, Stopped);
        Assert.Equal(new[] { "save", "snapshot" }, events);
        var stop = coordinator.ExecuteMemberAsync(server.Id, LifecycleAction.Stop);
        Assert.False(stop.IsCompleted);
        Assert.False(captured!.Disposed);
        await lease.DisposeAsync();
        Assert.True(captured.Disposed); Assert.True((await stop).Success);
        await lease.DisposeAsync(); // Repeated disposal must not release the shared gate twice.
    }

    [Fact]
    public async Task StoppedRetryUsesVerifiedLiveStateWithoutSavingOrStarting()
    {
        var (server, coordinator) = await SetupAsync();
        var lease = await coordinator.AcquireBackupStateAsync(server,
            (_, _) => throw new InvalidOperationException("Stopped world must not receive RCON"),
            (_, _) => throw new InvalidOperationException("Stopped world must not require VSS"), Stopped);
        Assert.Equal(Path.Combine(server.Directory, "ShooterGame", "Saved"), lease.Directory);
        var start = coordinator.ExecuteMemberAsync(server.Id, LifecycleAction.Start);
        Assert.False(start.IsCompleted);
        await lease.DisposeAsync();
        Assert.True((await start).Success);
    }

    [Fact]
    public async Task WrongSnapshotIsReleasedAndFailureDoesNotLeaveLifecycleGateLocked()
    {
        var (server, coordinator) = await SetupAsync();
        await coordinator.ExecuteMemberAsync(server.Id, LifecycleAction.Start);
        var wrong = new Captured(Path.Combine(root, "other"), Path.Combine(root, "immutable"));
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.AcquireBackupStateAsync(server,
            (_, _) => Task.CompletedTask, (_, _) => Task.FromResult<IStableServerState>(wrong), Stopped));
        Assert.True(wrong.Disposed);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.True((await coordinator.ExecuteMemberAsync(server.Id, LifecycleAction.Stop, deadline.Token)).Success);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
