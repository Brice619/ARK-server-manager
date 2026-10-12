using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class SettingsWorkflowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-settings-test-" + Guid.NewGuid().ToString("N"));
    private CatalogRepository Repository => new(Path.Combine(root, "catalog.json"));
    private static ServerSettings Settings(string name, int offset = 0) => new()
    { Name = name, GamePort = 7777 + offset, QueryPort = 27015 + offset, RconPort = 32330 + offset };

    [Fact]
    public async Task LifecycleCallbacksCanApplyPendingSettingsWithoutLockRecursion()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", "server", Settings("Old"))));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var events = new List<string>();
        SettingsChangeWorkflow workflow = null!;
        workflow = new(Repository, Path.Combine(root, "pending.json"),
            async (_, ct) => { await workflow.ApplyBeforeStartAsync(id, ct); events.Add("stop"); return true; },
            async (_, ct) => { await workflow.ApplyBeforeStartAsync(id, ct); events.Add("start"); },
            (_, _, _) => { events.Add("write"); return Task.CompletedTask; });
        var result = await workflow.SaveAsync(id, Settings("New"), true, SaveChoice.SaveAndRestart, deadline.Token);
        Assert.True(result.Restarted);
        Assert.Equal(new[] { "stop", "write", "start" }, events);
    }

    [Fact]
    public async Task ChangeDuringShutdownPreventsWritingAnUnreviewedServer()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", "server", Settings("Old"))));
        var workflow = new SettingsChangeWorkflow(Repository, Path.Combine(root, "pending.json"),
            async (_, ct) => { await Repository.ChangeAsync(c => c.UpdateSettings(id, Settings("Other")), ct); return true; },
            (_, _) => throw new InvalidOperationException("Must not restart"),
            (_, _, _) => throw new InvalidOperationException("Must not write"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.SaveAsync(id, Settings("New"), true, SaveChoice.SaveAndRestart));
        Assert.Contains("reviewed server changed", error.Message);
        Assert.Equal("Other", Assert.Single((await Repository.ReadAsync()).Servers).Settings.Name);
    }

    [Fact]
    public async Task ConfigurationAdapterCanReadCatalogWithoutDeadlocking()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", "server", Settings("Old"))));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var read = false;
        var workflow = new SettingsChangeWorkflow(Repository, Path.Combine(root, "pending.json"),
            (_, _) => Task.FromResult(true), (_, _) => Task.CompletedTask, async (serverId, settings, ct) =>
            {
                var current = await Repository.ReadAsync(ct);
                Assert.Equal("Old", current.Servers.Single(s => s.Id == serverId).Settings.Name);
                Assert.Equal("New", settings.Name);
                read = true;
            });
        var result = await workflow.SaveAsync(id, Settings("New"), false, SaveChoice.SaveForNextStart, deadline.Token);
        Assert.True(read);
        Assert.True(result.Saved);
        Assert.Equal("New", Assert.Single((await Repository.ReadAsync()).Servers).Settings.Name);
    }

    [Fact]
    public async Task ConcurrentCatalogChangeIsNotOverwrittenAndPendingIntentSurvives()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", "server", Settings("Old"))));
        var pendingPath = Path.Combine(root, "pending.json");
        var workflow = new SettingsChangeWorkflow(Repository, pendingPath,
            (_, _) => Task.FromResult(true), (_, _) => Task.CompletedTask,
            (_, _, ct) => Repository.ChangeAsync(c => c.UpdateSettings(id, Settings("Other edit")), ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.SaveAsync(id, Settings("Requested"), false, SaveChoice.SaveForNextStart));
        Assert.Equal("Other edit", Assert.Single((await Repository.ReadAsync()).Servers).Settings.Name);
        var pending = await new AtomicJsonStore<PendingSettingsDocument>(pendingPath).LoadAsync();
        Assert.Equal("Requested", Assert.Single(pending!.Changes).Settings.Name);
    }

    [Fact]
    public async Task PendingSettingsSurviveReloadAndDoNotAffectRunningConfiguration()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", "server", Settings("Old"))));
        var writes = new List<string>();
        SettingsChangeWorkflow Workflow() => new(Repository, Path.Combine(root, "pending.json"),
            (_, _) => throw new InvalidOperationException("Must not stop"), (_, _) => throw new InvalidOperationException("Must not start"),
            (_, settings, _) => { writes.Add(settings.Name); return Task.CompletedTask; });
        var result = await Workflow().SaveAsync(id, Settings("New"), true, SaveChoice.SaveForNextStart);
        Assert.True(result.Pending);
        Assert.Empty(writes);
        Assert.Equal("Old", Assert.Single((await Repository.ReadAsync()).Servers).Settings.Name);
        await Workflow().ApplyBeforeStartAsync(id);
        Assert.Equal("New", Assert.Single(writes));
        Assert.Equal("New", Assert.Single((await Repository.ReadAsync()).Servers).Settings.Name);
        await Workflow().ApplyBeforeStartAsync(id);
        Assert.Single(writes);
    }

    [Fact]
    public async Task RestartSaveTargetsOnlySelectedMapAndStopsBeforeWriting()
    {
        var clusterId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await Repository.ChangeAsync(c =>
        {
            c.AddCluster(new(clusterId, "Cluster", ArkEdition.ASE, Settings("Cluster")));
            c.AddServer(new(first, ArkEdition.ASE, "TheIsland", "first", Settings("First"), clusterId));
            c.AddServer(new(second, ArkEdition.ASE, "ScorchedEarth_P", "second", Settings("Second", 10), clusterId));
        });
        var events = new List<string>();
        var workflow = new SettingsChangeWorkflow(Repository, Path.Combine(root, "pending.json"),
            (ids, _) => { Assert.Equal(first, Assert.Single(ids)); events.Add("stop"); return Task.FromResult(true); },
            (ids, _) => { Assert.Equal(first, Assert.Single(ids)); events.Add("start"); return Task.CompletedTask; },
            (_, _, _) => { events.Add("write"); return Task.CompletedTask; });
        await workflow.SaveAsync(first, Settings("Changed"), true, SaveChoice.Cancel);
        Assert.Empty(events);
        var result = await workflow.SaveAsync(first, Settings("Changed"), true, SaveChoice.SaveAndRestart);
        Assert.True(result.Restarted);
        Assert.Equal(new[] { "stop", "write", "start" }, events);
        Assert.Equal("Second", (await Repository.ReadAsync()).Servers.Single(s => s.Id == second).Settings.Name);
    }

    [Fact]
    public async Task FailedConfigurationWriteRetainsPendingSettingsForRecovery()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", "server", Settings("Old"))));
        var fail = true;
        var workflow = new SettingsChangeWorkflow(Repository, Path.Combine(root, "pending.json"), (_, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask, (_, _, _) => fail ? throw new IOException("Write failed") : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => workflow.SaveAsync(id, Settings("New"), false, SaveChoice.SaveAndRestart));
        Assert.Equal("Old", Assert.Single((await Repository.ReadAsync()).Servers).Settings.Name);
        fail = false;
        await workflow.ApplyBeforeStartAsync(id);
        Assert.Equal("New", Assert.Single((await Repository.ReadAsync()).Servers).Settings.Name);
    }

    [Fact]
    public async Task FailedShutdownLeavesSettingsUnchanged()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", "server", Settings("Old"))));
        var workflow = new SettingsChangeWorkflow(Repository, Path.Combine(root, "pending.json"), (_, _) => Task.FromResult(false),
            (_, _) => throw new InvalidOperationException(), (_, _, _) => throw new InvalidOperationException());
        var result = await workflow.SaveAsync(id, Settings("New"), true, SaveChoice.SaveAndRestart);
        Assert.False(result.Saved);
        Assert.Equal("Old", Assert.Single((await Repository.ReadAsync()).Servers).Settings.Name);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"serverId\":\"00000000-0000-0000-0000-000000000000\",\"settings\":null}")]
    [InlineData("{\"serverId\":\"11111111-1111-1111-1111-111111111111\",\"settings\":{\"name\":\"Invalid ports\"}}")]
    public async Task InvalidPendingRecordsPreventConfigurationWrites(string record)
    {
        Directory.CreateDirectory(root);
        var pendingPath = Path.Combine(root, "pending.json");
        await File.WriteAllTextAsync(pendingPath, "{\"schemaVersion\":1,\"changes\":[" + record + "]}");
        var workflow = new SettingsChangeWorkflow(Repository, pendingPath,
            (_, _) => throw new InvalidOperationException("Must not stop"),
            (_, _) => throw new InvalidOperationException("Must not start"),
            (_, _, _) => throw new InvalidOperationException("Must not write"));
        await Assert.ThrowsAsync<InvalidDataException>(() => workflow.ApplyBeforeStartAsync(Guid.NewGuid()));
        Assert.Contains(record, await File.ReadAllTextAsync(pendingPath));
    }

    [Fact]
    public async Task ImmediateSaveAcquiresStoppedScopeBeforePendingLockAndKeepsCommitInsideScope()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", "server", Settings("Old"))));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var inScope = false;
        var scopeCalls = 0;
        SettingsChangeWorkflow workflow = null!;
        workflow = new(Repository, Path.Combine(root, "pending.json"),
            (_, _) => Task.FromResult(true), (_, _) => Task.CompletedTask,
            (_, _, _) => { Assert.True(inScope); return Task.CompletedTask; },
            async (_, change, ct) =>
            {
                scopeCalls++;
                // A competing startup can complete its pending hook before yielding the lifecycle gate.
                await workflow.ApplyBeforeStartAsync(id, ct);
                inScope = true;
                try
                {
                    await change(ct);
                    Assert.Equal("New", Assert.Single((await Repository.ReadAsync(ct)).Servers).Settings.Name);
                }
                finally { inScope = false; }
            });
        Assert.True((await workflow.SaveAsync(id, Settings("New"), false, SaveChoice.SaveForNextStart, deadline.Token)).Saved);
        Assert.Equal(1, scopeCalls);
        Assert.True((await workflow.SaveAsync(id, Settings("Later"), true, SaveChoice.SaveForNextStart, deadline.Token)).Pending);
        Assert.Equal(1, scopeCalls);
    }

    [Fact]
    public async Task QueuedReviewHashSurvivesReloadAndConflictRetainsPendingIntent()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", "server", Settings("Old"))));
        var pendingPath = Path.Combine(root, "pending.json");
        var reviewedHash = new string('A', 64);
        var fail = true;
        SettingsChangeWorkflow Create() => new(Repository, pendingPath,
            (_, _) => Task.FromResult(true), (_, _) => Task.CompletedTask,
            (_, _, _) => throw new InvalidOperationException("Unreviewed writer must not run"),
            writeReviewedConfiguration: (_, _, hash, _) =>
            {
                Assert.Equal(reviewedHash, hash);
                if (fail) throw new IOException("Configuration changed");
                return Task.CompletedTask;
            });
        Assert.True((await Create().SaveAsync(id, Settings("New"), true, SaveChoice.SaveForNextStart,
            sourceSHA256: reviewedHash)).Pending);
        await Assert.ThrowsAsync<IOException>(() => Create().ApplyBeforeStartAsync(id));
        Assert.Equal("Old", Assert.Single((await Repository.ReadAsync()).Servers).Settings.Name);
        var pending = Assert.Single((await new AtomicJsonStore<PendingSettingsDocument>(pendingPath).LoadAsync())!.Changes);
        Assert.Equal(reviewedHash, pending.SourceSHA256);
        Assert.Equal("New", pending.Settings.Name);
        var detached = await Create().ReadPendingAsync(id);
        detached!.Settings.Values["Temporary"] = "not persisted";
        Assert.Empty((await Create().ReadPendingAsync(id))!.Settings.Values);
        fail = false;
        await Create().ApplyBeforeStartAsync(id);
        Assert.Equal("New", Assert.Single((await Repository.ReadAsync()).Servers).Settings.Name);
        Assert.Empty((await new AtomicJsonStore<PendingSettingsDocument>(pendingPath).LoadAsync())!.Changes);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
