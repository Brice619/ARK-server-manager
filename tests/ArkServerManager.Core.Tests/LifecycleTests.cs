using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class LifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-lifecycle-" + Guid.NewGuid().ToString("N"));
    private CatalogRepository Repository => new(Path.Combine(root, "catalog.json"));
    private readonly Guid first = Guid.NewGuid(), second = Guid.NewGuid();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupJoiningRecoveryExpandsGroupStartButPreservesMemberOnlyScope(bool memberOnly)
    {
        await SeedAsync();
        var cluster = (await Repository.ReadAsync()).Clusters.Single().Id;
        await Repository.ChangeAsync(c => c.SetClusterMembership([first, second], null));
        var runtimes = new Dictionary<Guid, FakeRuntime>(); var recovered = false;
        var coordinator = new ServerLifecycleCoordinator(Repository, id => runtimes[id] = new FakeRuntime(), async (_, ct) =>
        {
            if (!recovered) { await Repository.ChangeAsync(c => c.SetClusterMembership([first, second], cluster), ct); recovered = true; }
        });
        var result = memberOnly ? await coordinator.ExecuteMemberAsync(first, LifecycleAction.Start)
            : await coordinator.ExecuteAsync(first, LifecycleAction.Start);
        Assert.True(result.Success); Assert.Equal(memberOnly ? 1 : 2, result.Members.Count);
        Assert.True(runtimes.ContainsKey(first)); Assert.Equal(!memberOnly, runtimes.ContainsKey(second));
    }

    [Fact]
    public async Task EditingClusterDefaultsPreservesExistingMapSettingsAndPorts()
    {
        await SeedAsync();
        var before = await Repository.ReadAsync(); var cluster = before.Clusters.Single();
        await Repository.ChangeAsync(c => c.UpdateClusterDefaults(cluster.Id, "Renamed cluster", 90));
        var after = await Repository.ReadAsync();
        Assert.Equal("Renamed cluster", after.Clusters.Single().Name);
        Assert.Equal(90, after.Clusters.Single().Settings.MaxPlayers);
        Assert.Equal(cluster.Settings.GamePort, after.Clusters.Single().Settings.GamePort);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before.Servers), System.Text.Json.JsonSerializer.Serialize(after.Servers));
    }

    [Fact]
    public async Task ScheduledRestartNeverStartsStoppedMapAndLeavesRunningSiblingAlone()
    {
        await SeedAsync(); var runtimes = new Dictionary<Guid, FakeRuntime>();
        var coordinator = new ServerLifecycleCoordinator(Repository, id => runtimes[id] = new FakeRuntime(), (_, _) => Task.CompletedTask);
        Assert.True((await coordinator.RestartRunningMemberAsync(first)).Success);
        Assert.Empty(runtimes);
        await coordinator.ExecuteAsync(first, LifecycleAction.Start);
        await coordinator.RestartRunningMemberAsync(first);
        Assert.Equal(2, runtimes[first].Starts); Assert.Equal(1, runtimes[second].Starts);
        Assert.Equal(ServerState.Running, runtimes[second].State);
        await coordinator.ExecuteMemberAsync(first, LifecycleAction.Stop);
        await coordinator.RestartRunningMemberAsync(first);
        Assert.Equal(2, runtimes[first].Starts); Assert.Equal(ServerState.Stopped, runtimes[first].State);
    }

    [Fact]
    public async Task StoppedUpdateHookRunsOnlyAfterSelectedMemberSuccessfullyStops()
    {
        await SeedAsync(); var runtime = new FakeRuntime(); var updated = new List<Guid>();
        var coordinator = new ServerLifecycleCoordinator(Repository, _ => runtime, (_, _) => Task.CompletedTask,
            afterVerifiedStop: (server, _) => { updated.Add(server.Id); return Task.CompletedTask; });
        await coordinator.ExecuteMemberAsync(first, LifecycleAction.Start);
        runtime.FailStop = true;
        Assert.False((await coordinator.ExecuteMemberAsync(first, LifecycleAction.Stop)).Success);
        Assert.Empty(updated);
        runtime.FailStop = false;
        Assert.True((await coordinator.ExecuteMemberAsync(first, LifecycleAction.Stop)).Success);
        Assert.Equal(new[] { first }, updated);
    }

    [Fact]
    public async Task MaintenanceStatusScopeExcludesStopAndReleasesAfterFailure()
    {
        await SeedAsync();
        var coordinator = new ServerLifecycleCoordinator(Repository, _ => new FakeRuntime(), (_, _) => Task.CompletedTask);
        await coordinator.ExecuteMemberAsync(first, LifecycleAction.Start);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var maintenance = coordinator.WithStatusScopeAsync(async (statuses, ct) =>
        {
            Assert.True(statuses.Single(s => s.ServerId == first).Managed);
            Assert.Equal(ServerState.Running, statuses.Single(s => s.ServerId == first).State);
            Assert.False(statuses.Single(s => s.ServerId == second).Managed);
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            throw new IOException("Fixture maintenance failure");
        }, timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var stopping = coordinator.ExecuteMemberAsync(first, LifecycleAction.Stop, timeout.Token);
        Assert.False(stopping.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => maintenance);
        Assert.True((await stopping).Success);
        Assert.Equal(ServerState.Stopped, (await coordinator.StatusAsync(timeout.Token)).Single(s => s.ServerId == first).State);
    }

    private async Task SeedAsync()
    {
        var cluster = Guid.NewGuid();
        ServerSettings Settings(string name, int offset) => new()
        { Name = name, GamePort = 7777 + offset, QueryPort = 27015 + offset, RconPort = 32330 + offset };
        await Repository.ChangeAsync(c =>
        {
            c.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, Settings("Cluster", 0)));
            c.AddServer(new(first, ArkEdition.ASE, "TheIsland", "first", Settings("First", 0), cluster));
            c.AddServer(new(second, ArkEdition.ASE, "ScorchedEarth_P", "second", Settings("Second", 10), cluster));
        });
    }

    [Fact]
    public async Task StartsEntireClusterAfterPendingChangesAndUsesFreshDefinitions()
    {
        await SeedAsync();
        var runtimes = new Dictionary<Guid, FakeRuntime>();
        var coordinator = new ServerLifecycleCoordinator(Repository,
            id => runtimes[id] = new(), async (id, ct) =>
            {
                var settings = (await Repository.ReadAsync(ct)).Servers.Single(s => s.Id == id).Settings;
                await Repository.ChangeAsync(c => c.UpdateSettings(id, settings with { Name = "Applied" }), ct);
            });
        var result = await coordinator.ExecuteAsync(first, LifecycleAction.Start);
        Assert.True(result.Success);
        Assert.Equal(2, result.Members.Count);
        Assert.All(runtimes.Values, runtime => Assert.Equal("Applied", runtime.Definition!.Settings.Name));
        await coordinator.ExecuteAsync(second, LifecycleAction.Start);
        Assert.All(runtimes.Values, runtime => Assert.Equal(1, runtime.Starts));
    }

    [Fact]
    public async Task FailedMemberDoesNotPreventOtherMemberStarting()
    {
        await SeedAsync();
        var coordinator = new ServerLifecycleCoordinator(Repository,
            id => new FakeRuntime { FailStart = id == first }, (_, _) => Task.CompletedTask);
        var result = await coordinator.ExecuteAsync(first, LifecycleAction.Start);
        Assert.False(result.Success);
        Assert.False(result.Members.Single(m => m.ServerId == first).Success);
        Assert.Equal(ServerState.Running, result.Members.Single(m => m.ServerId == second).State);
    }

    [Fact]
    public async Task RestartStopsMembersBeforeLaunchingAndNeverDuplicatesFailedStop()
    {
        await SeedAsync();
        var events = new List<string>();
        var runtimes = new Dictionary<Guid, FakeRuntime>();
        var coordinator = new ServerLifecycleCoordinator(Repository,
            id => runtimes[id] = new() { Event = value => events.Add(id + ":" + value) }, (_, _) => Task.CompletedTask);
        await coordinator.ExecuteAsync(first, LifecycleAction.Start);
        events.Clear();
        runtimes[first].FailStop = true;
        var result = await coordinator.ExecuteAsync(second, LifecycleAction.Restart);
        Assert.False(result.Success);
        Assert.Equal(new[] { first + ":stop", second + ":stop", second + ":start" }, events);
        Assert.Equal(1, runtimes[first].Starts);
        Assert.Equal(2, runtimes[second].Starts);
    }

    [Fact]
    public async Task ConcurrentStartsCannotLaunchSameMemberTwice()
    {
        await SeedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimes = new Dictionary<Guid, FakeRuntime>();
        var coordinator = new ServerLifecycleCoordinator(Repository, id => runtimes[id] = new(), async (id, ct) =>
        {
            if (id == first) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
        });
        var firstStart = coordinator.ExecuteAsync(first, LifecycleAction.Start);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrent = coordinator.ExecuteAsync(second, LifecycleAction.Start);
        Assert.False(concurrent.IsCompleted);
        release.SetResult();
        await Task.WhenAll(firstStart, concurrent);
        Assert.All(runtimes.Values, runtime => Assert.Equal(1, runtime.Starts));
    }

    [Fact]
    public async Task StoppedFileScopeChecksEveryClusterMemberAndExcludesConcurrentStart()
    {
        await SeedAsync();
        var coordinator = new ServerLifecycleCoordinator(Repository, _ => new FakeRuntime(), (_, _) => Task.CompletedTask);
        var selected = (await Repository.ReadAsync()).Servers.Single(s => s.Id == first);
        var checkedIds = new List<Guid>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = coordinator.WithStoppedServerAsync(selected, async _ => { entered.SetResult(); await release.Task; },
            (server, _) =>
            {
                checkedIds.Add(server.Id);
                return Task.FromResult(new InstallationStopCheck(Path.GetFullPath(server.Directory), InstallationStopState.Stopped,
                    [], [], DateTimeOffset.UtcNow));
            });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var start = coordinator.ExecuteAsync(first, LifecycleAction.Start);
        try
        {
            Assert.False(start.IsCompleted);
            Assert.Equal(new[] { first, second }.Order(), checkedIds.Order());
        }
        finally { release.TrySetResult(); }
        await scope;
        Assert.True((await start).Success);
    }

    [Theory]
    [InlineData(InstallationStopState.Running)]
    [InlineData(InstallationStopState.Unverified)]
    public async Task ObservedRunningOrUnknownClusterMemberPreventsFileChange(InstallationStopState observed)
    {
        await SeedAsync();
        var coordinator = new ServerLifecycleCoordinator(Repository, _ => new FakeRuntime(), (_, _) => Task.CompletedTask);
        var selected = (await Repository.ReadAsync()).Servers.Single(s => s.Id == first);
        var changed = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.WithStoppedServerAsync(selected,
            _ => { changed = true; return Task.CompletedTask; }, (server, _) => Task.FromResult(new InstallationStopCheck(
                Path.GetFullPath(server.Directory), server.Id == second ? observed : InstallationStopState.Stopped,
                [], [], DateTimeOffset.UtcNow))));
        Assert.False(changed);
        Assert.True((await coordinator.ExecuteAsync(first, LifecycleAction.Start)).Success);
    }

    [Fact]
    public async Task StopAllContinuesAfterFailedStopAndDoesNotCreateUnownedRuntimes()
    {
        await SeedAsync();
        var runtimes = new Dictionary<Guid, FakeRuntime>();
        var coordinator = new ServerLifecycleCoordinator(Repository,
            id => runtimes[id] = new(), (_, _) => Task.CompletedTask);
        Assert.Empty((await coordinator.StopAllAsync()).Members);
        Assert.Empty(runtimes);
        await coordinator.ExecuteAsync(first, LifecycleAction.Start);
        runtimes[first].FailStop = true;
        var result = await coordinator.StopAllAsync();
        Assert.False(result.Success);
        Assert.Equal(2, result.Members.Count);
        Assert.Equal(ServerState.Running, result.Members.Single(m => m.ServerId == first).State);
        Assert.Equal(ServerState.Stopped, result.Members.Single(m => m.ServerId == second).State);
        Assert.All(runtimes.Values, runtime => Assert.Equal(1, runtime.Starts));
    }

    [Fact]
    public async Task MemberFileScopeAllowsRunningSiblingAndChecksOnlySelectedMap()
    {
        await SeedAsync();
        var runtimes = new Dictionary<Guid, FakeRuntime>();
        var coordinator = new ServerLifecycleCoordinator(Repository,
            id => runtimes[id] = new(), (_, _) => Task.CompletedTask);
        await coordinator.ExecuteAsync(first, LifecycleAction.Start);
        var stop = await coordinator.ExecuteMemberAsync(first, LifecycleAction.Stop);
        Assert.True(stop.Success);
        Assert.Equal(first, Assert.Single(stop.Members).ServerId);
        var selected = (await Repository.ReadAsync()).Servers.Single(s => s.Id == first);
        var checkedIds = new List<Guid>();
        var changed = false;
        await coordinator.WithStoppedMemberAsync(selected,
            _ => { changed = true; return Task.CompletedTask; }, (server, _) =>
            {
                checkedIds.Add(server.Id);
                return Task.FromResult(new InstallationStopCheck(Path.GetFullPath(server.Directory),
                    InstallationStopState.Stopped, [], [], DateTimeOffset.UtcNow));
            });
        Assert.True(changed);
        Assert.Equal(new[] { first }, checkedIds);
        Assert.Equal(ServerState.Running, runtimes[second].State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.WithStoppedServerAsync(selected,
            _ => Task.CompletedTask, (server, _) => Task.FromResult(new InstallationStopCheck(
                Path.GetFullPath(server.Directory), InstallationStopState.Stopped, [], [], DateTimeOffset.UtcNow))));
    }

    private sealed class FakeRuntime : IServerRuntime
    {
        public ServerState State { get; private set; } = ServerState.Stopped;
        public ServerDefinition? Definition { get; private set; }
        public bool FailStart { get; init; }
        public bool FailStop { get; set; }
        public int Starts { get; private set; }
        public Action<string>? Event { get; init; }
        public Task StartAsync(ServerDefinition definition, CancellationToken ct)
        {
            Event?.Invoke("start");
            Starts++;
            Definition = definition;
            if (FailStart) { State = ServerState.Failed; throw new IOException("Startup failed"); }
            State = ServerState.Running;
            return Task.CompletedTask;
        }
        public Task<bool> StopAsync(CancellationToken ct)
        {
            Event?.Invoke("stop");
            if (FailStop) return Task.FromResult(false);
            State = ServerState.Stopped;
            return Task.FromResult(true);
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
