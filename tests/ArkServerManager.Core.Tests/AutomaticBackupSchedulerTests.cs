using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class AutomaticBackupSchedulerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-backup-schedule-" + Guid.NewGuid().ToString("N"));
    private sealed class Stable(string directory) : IStableServerState
    {
        public string Directory => directory;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task EmptyCatalogStillRunsIndependentCleanupOncePerCycle()
    {
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var cleanups = 0; var serverTicks = 0;
        var scheduler = new AutomaticBackupScheduler(repository,
            _ => Task.FromResult<IReadOnlyList<MemberOperationResult>>([]),
            (_, _, _, _) => { serverTicks++; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask,
            maintenance: _ => { cleanups++; return Task.CompletedTask; });
        await scheduler.RunCycleAsync(); await scheduler.RunCycleAsync();
        Assert.Equal(2, cleanups); Assert.Equal(0, serverTicks);
    }

    [Fact]
    public async Task FailedIndependentCleanupDoesNotBlockARealServerArchive()
    {
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "server"),
            new() { Name = "Server", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        await repository.ChangeAsync(catalog => catalog.AddServer(server));
        var source = Path.Combine(root, "state"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "world.ark"), "saved world");
        var reports = new List<string>();
        var scheduler = new AutomaticBackupScheduler(repository,
            _ => Task.FromResult<IReadOnlyList<MemberOperationResult>>([new(server.Id, ServerState.Running, true, null)
                { Managed = true, RunningTicks = TimeSpan.FromHours(1).Ticks }]),
            async (selected, observation, now, ct) =>
            {
                var service = new AutomaticBackupService(selected, TimeSpan.FromMinutes(1),
                    _ => Task.FromResult<IStableServerState>(new Stable(source)), _ => Task.CompletedTask);
                await service.TickMeasuredAsync(observation!, now, ct);
            }, (_, message) => { reports.Add(message); return Task.CompletedTask; },
            maintenance: _ => throw new IOException("Cleanup permission unavailable"));
        await scheduler.RunCycleAsync();
        Assert.Single(reports); Assert.Contains("Cleanup permission unavailable", reports[0]);
        var state = await AutomaticBackupService.ReadRecordedAsync(server);
        var point = Assert.Single(state.History);
        Assert.Null(state.PendingHour);
        Assert.Equal(TimeSpan.FromHours(1).Ticks, state.RuntimeTicks);
        await new SnapshotArchive().VerifyAsync(Path.Combine(server.Directory, "backups", point.FileName));
    }

    [Fact]
    public async Task OneMemberFailureDoesNotPreventAnotherBackupAndRetriesDoNotDoubleCount()
    {
        var first = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "first"), new() { Name = "First", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        var second = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "second"), new() { Name = "Second", GamePort = 7787, QueryPort = 27025, RconPort = 32340 });
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        await repository.ChangeAsync(document => { document.AddServer(first); document.AddServer(second); });
        var source = Path.Combine(root, "state"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "world.ark"), "saved world");
        var statuses = new List<MemberOperationResult>
        {
            new(first.Id, ServerState.Running, true, null) { Managed = true, RunningTicks = TimeSpan.FromHours(1).Ticks },
            new(second.Id, ServerState.Running, true, null) { Managed = true, RunningTicks = TimeSpan.FromHours(1).Ticks }
        };
        var clock = new Clock(); var fail = true;
        var scheduler = new AutomaticBackupScheduler(repository, _ => Task.FromResult<IReadOnlyList<MemberOperationResult>>(statuses),
            server => new(server, TimeSpan.FromMinutes(1), _ => server.Id == first.Id && fail
                ? throw new IOException("Unavailable disk") : Task.FromResult<IStableServerState>(new Stable(source)), _ => Task.CompletedTask),
            (_, _) => Task.CompletedTask, clock);
        await scheduler.RunCycleAsync();
        Assert.Equal(1, (await AutomaticBackupService.ReadRecordedAsync(first)).PendingHour);
        Assert.Single((await AutomaticBackupService.ReadRecordedAsync(second)).History);
        fail = false; clock.Now += TimeSpan.FromMinutes(1);
        await scheduler.RunCycleAsync();
        foreach (var server in new[] { first, second })
        {
            var state = await AutomaticBackupService.ReadRecordedAsync(server);
            Assert.Single(state.History); Assert.Null(state.PendingHour);
            Assert.Equal(TimeSpan.FromHours(1).Ticks, state.RuntimeTicks);
        }
    }

    [Fact]
    public async Task UnownedRuntimeDoesNotAccumulateInferredUptime()
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "server"), new() { Name = "Server", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        await repository.ChangeAsync(document => document.AddServer(server));
        var captures = 0;
        var scheduler = new AutomaticBackupScheduler(repository,
            _ => Task.FromResult<IReadOnlyList<MemberOperationResult>>([new(server.Id, ServerState.Running, true, null)]),
            selected => new(selected, TimeSpan.FromMinutes(1), _ => { captures++; throw new IOException("Must not capture"); }, _ => Task.CompletedTask),
            (_, _) => Task.CompletedTask);
        await scheduler.RunCycleAsync();
        Assert.Equal(0, captures);
        Assert.Equal(0, (await AutomaticBackupService.ReadRecordedAsync(server)).RuntimeTicks);
    }

    [Fact]
    public async Task CorruptMemberHistoryIsReportedWithoutChangingItOrBlockingHealthyMember()
    {
        var broken = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "broken"),
            new() { Name = "Broken", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        var healthy = broken with { Id = Guid.NewGuid(), Directory = Path.Combine(root, "healthy"),
            Settings = new() { Name = "Healthy", GamePort = 7787, QueryPort = 27025, RconPort = 32340 } };
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        await repository.ChangeAsync(c => { c.AddServer(broken); c.AddServer(healthy); });
        var historyPath = Path.Combine(broken.Directory, "backups", "backup-history.json");
        await new AtomicJsonStore<AutomaticBackupState>(historyPath).SaveAsync(
            new(1, broken.Id, broken.Edition, 0, null, null, [], null) { Scope = SnapshotScope.SharedCluster });
        var original = await File.ReadAllBytesAsync(historyPath);
        var source = Path.Combine(root, "state"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "world.ark"), "world fixture");
        var messages = new System.Collections.Concurrent.ConcurrentBag<Guid?>();
        var statuses = new[] { broken, healthy }.Select(s => new MemberOperationResult(s.Id, ServerState.Running, true, null)
            { Managed = true, RunningTicks = TimeSpan.FromHours(1).Ticks }).ToArray();
        var scheduler = new AutomaticBackupScheduler(repository, _ => Task.FromResult<IReadOnlyList<MemberOperationResult>>(statuses),
            s => new(s, TimeSpan.FromMinutes(1), _ => Task.FromResult<IStableServerState>(new Stable(source)), _ => Task.CompletedTask),
            (id, _) => { messages.Add(id); return Task.CompletedTask; });
        await scheduler.RunCycleAsync();
        Assert.Equal(broken.Id, Assert.Single(messages));
        Assert.Equal(original, await File.ReadAllBytesAsync(historyPath));
        Assert.Single((await AutomaticBackupService.ReadRecordedAsync(healthy)).History);
    }

    [Fact]
    public async Task SharedClusterSchedulingUsesOneMeasuredClockAndRetriesWithoutAgingStoppedTime()
    {
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var first = new ClusterDefinition(Guid.NewGuid(), "First", ArkEdition.ASE, new());
        var second = first with { Id = Guid.NewGuid(), Name = "Second" };
        await repository.ChangeAsync(c => { c.AddCluster(first); c.AddCluster(second); });
        var catalog = await repository.ReadAsync();
        foreach (var cluster in new[] { first, second })
        {
            var storage = ManagedClusterStorage.Prepare(catalog, cluster.Id, root);
            await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "survivor"), "shared player state");
        }
        var clock = new Clock(); var fail = true;
        var observations = new System.Collections.Concurrent.ConcurrentBag<RunningTimeObservation>();
        var scheduler = new AutomaticBackupScheduler(repository,
            _ => Task.FromResult<IReadOnlyList<MemberOperationResult>>([]),
            (_, _, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, clock,
            _ => TimeSpan.FromHours(1), async (document, cluster, observation, now, ct) =>
            {
                observations.Add(observation);
                var transfers = ManagedClusterStorage.Resolve(document, cluster.Id, root).Transfers;
                var service = new AutomaticBackupService(document, cluster.Id, root, TimeSpan.FromMinutes(1),
                    _ => cluster.Id == first.Id && fail ? throw new IOException("Unavailable shared view")
                        : Task.FromResult<IStableServerState>(new Stable(transfers)), _ => Task.CompletedTask);
                await service.TickMeasuredAsync(observation, now, ct);
            });
        await scheduler.RunCycleAsync();
        Assert.Equal(1, (await AutomaticBackupService.ReadRecordedClusterAsync(catalog, first.Id, root)).PendingHour);
        Assert.Single((await AutomaticBackupService.ReadRecordedClusterAsync(catalog, second.Id, root)).History);
        fail = false; clock.Now += TimeSpan.FromDays(3);
        await scheduler.RunCycleAsync();
        foreach (var cluster in new[] { first, second })
        {
            var state = await AutomaticBackupService.ReadRecordedClusterAsync(catalog, cluster.Id, root);
            Assert.Single(state.History); Assert.Null(state.PendingHour);
            Assert.Equal(TimeSpan.FromHours(1).Ticks, state.RuntimeTicks);
            Assert.Equal(SnapshotScope.SharedCluster, state.Scope);
        }
        Assert.Single(observations.Select(o => o.HostSession).Distinct());
        Assert.All(observations, o => Assert.Equal(TimeSpan.FromHours(1).Ticks, o.CumulativeTicks));
    }

    [Fact]
    public async Task ClusterSchedulingFailureDoesNotBlockOtherClustersOrMemberWork()
    {
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var first = new ClusterDefinition(Guid.NewGuid(), "Broken", ArkEdition.ASE, new());
        var second = first with { Id = Guid.NewGuid(), Name = "Healthy" };
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "server"),
            new() { Name = "Server", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        await repository.ChangeAsync(c => { c.AddCluster(first); c.AddCluster(second); c.AddServer(server); });
        var completed = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var notices = new System.Collections.Concurrent.ConcurrentBag<string>();
        var scheduler = new AutomaticBackupScheduler(repository,
            _ => Task.FromResult<IReadOnlyList<MemberOperationResult>>([]),
            (selected, _, _, _) => { completed.Add(selected.Id); return Task.CompletedTask; },
            (_, message) => { notices.Add(message); return Task.CompletedTask; }, readClusterRuntime: _ => TimeSpan.Zero,
            executeClusterTick: (_, cluster, _, _, _) =>
            {
                if (cluster.Id == first.Id) throw new IOException("Shared disk unavailable");
                completed.Add(cluster.Id); return Task.CompletedTask;
            });
        await scheduler.RunCycleAsync();
        Assert.Contains(server.Id, completed); Assert.Contains(second.Id, completed);
        Assert.DoesNotContain(first.Id, completed);
        Assert.Contains("Broken", Assert.Single(notices));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
