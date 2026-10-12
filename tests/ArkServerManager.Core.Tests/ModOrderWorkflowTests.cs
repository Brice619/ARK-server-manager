using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ModOrderWorkflowTests : IDisposable
{
    [Fact]
    public async Task VerifiedAsaRemovalRetriesCleanupBeforeClearingItsDurableChange()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASA, "TheIsland_WP", root,
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }) { Mods = ["1", "2"] }));
        var mods = Path.Combine(root, "ShooterGame", "Binaries", "Win64", "ShooterGame", "Mods", "83374");
        Directory.CreateDirectory(Path.Combine(mods, "1_10")); Directory.CreateDirectory(Path.Combine(mods, "2_20"));
        await File.WriteAllTextAsync(Path.Combine(mods, "1_10", "removed.pak"), "removed");
        await File.WriteAllTextAsync(Path.Combine(mods, "2_20", "kept.pak"), "kept");
        var attempts = 0;
        var workflow = new ModOrderWorkflow(Repository, Path.Combine(root, "pending.json"),
            (_, _) => Task.FromResult(true), (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask,
            removeFiles: async (server, removed, ct) =>
            {
                Assert.Equal(["1"], removed);
                if (++attempts == 1) throw new IOException("locked mod fixture");
                await RemovedAsaModFiles.DeleteAsync(server, removed, ct);
            }, supportsAsaRemoval: true);
        await Assert.ThrowsAsync<IOException>(() => workflow.SaveAsync(id, ["2"], false, SaveChoice.SaveForNextStart, replace: true));
        Assert.Equal(["2"], (await Repository.ReadAsync()).Servers.Single().Mods);
        Assert.True((await workflow.ReadAsync(id)).Replace);
        Assert.True(Directory.Exists(Path.Combine(mods, "1_10")));
        await workflow.ApplyBeforeStartAsync(id);
        Assert.Null((await workflow.ReadAsync(id)).Order);
        Assert.False(Directory.Exists(Path.Combine(mods, "1_10")));
        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(mods, "2_20", "kept.pak")));
    }

    [Fact]
    public async Task AsaAdditionAppliesAtStartupWithoutMetadataOrFileCleanup()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASA, "TheIsland_WP", root,
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }) { Mods = ["123"] }));
        var writes = 0;
        var workflow = new ModOrderWorkflow(Repository, Path.Combine(root, "pending.json"),
            (_, _) => throw new InvalidOperationException("No stop expected"),
            (_, _) => throw new InvalidOperationException("No start expected"),
            (_, ids, _) => { Assert.Equal(new[] { "123", "456" }, ids); writes++; return Task.CompletedTask; },
            removeFiles: (_, _, _) => throw new InvalidOperationException("No cleanup expected"));
        await workflow.SaveAsync(id, ["123", "456"], true, SaveChoice.SaveForNextStart, replace: true);
        Assert.Equal(0, writes);
        Assert.Equal(new[] { "123" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
        await workflow.ApplyBeforeStartAsync(id);
        Assert.Equal(1, writes);
        Assert.Equal(new[] { "123", "456" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
        Assert.Null((await workflow.ReadAsync(id)).Order);
        await Assert.ThrowsAsync<NotSupportedException>(() => workflow.SaveAsync(id, ["456"], false, SaveChoice.SaveAndRestart, replace: true));
        Assert.Equal(1, writes);
        Assert.Equal(new[] { "123", "456" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
    }

    [Fact]
    public async Task ReplacementRetainsCleanupUntilSuccessfulRetryAndPreservesOtherServerFiles()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", root,
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }) { Mods = ["1", "2"] }));
        var modRoot = Path.Combine(root, "ShooterGame", "Content", "Mods");
        Directory.CreateDirectory(Path.Combine(modRoot, "1")); Directory.CreateDirectory(Path.Combine(modRoot, "2"));
        await File.WriteAllTextAsync(Path.Combine(modRoot, "1", "old.asset"), "removed");
        await File.WriteAllTextAsync(Path.Combine(modRoot, "1.mod"), "removed descriptor");
        await File.WriteAllTextAsync(Path.Combine(modRoot, "2", "kept.asset"), "kept");
        var cache = Path.Combine(root, "Engine", "Binaries", "ThirdParty", "SteamCMD", "Win64", "steamapps", "workshop", "content", "346110", "1");
        Directory.CreateDirectory(cache); await File.WriteAllTextAsync(Path.Combine(cache, "download.asset"), "removed cache");
        var world = Path.Combine(root, "ShooterGame", "Saved", "SavedArks", "world.ark");
        Directory.CreateDirectory(Path.GetDirectoryName(world)!); await File.WriteAllTextAsync(world, "world stays");
        var cleanupAttempts = 0;
        var writes = new List<string>();
        var workflow = new ModOrderWorkflow(Repository, Path.Combine(root, "pending.json"), (_, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask, (_, order, _) => { writes.Add(string.Join(",", order)); return Task.CompletedTask; },
            removeFiles: async (server, removed, ct) =>
            {
                Assert.Equal(new[] { "1" }, removed);
                if (++cleanupAttempts == 1) throw new IOException("Fixture cleanup failure");
                await RemovedAseModFiles.DeleteAsync(server, removed, ct);
            });
        await Assert.ThrowsAsync<IOException>(() => workflow.SaveAsync(id, ["2", "3"], false, SaveChoice.SaveAndRestart, replace: true));
        Assert.Equal(new[] { "2", "3" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
        Assert.True((await workflow.ReadAsync(id)).Replace);
        Assert.True(File.Exists(Path.Combine(modRoot, "1.mod")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.SaveAsync(id, ["3", "2"], false, SaveChoice.SaveAndRestart));
        await workflow.ApplyBeforeStartAsync(id);
        Assert.Null((await workflow.ReadAsync(id)).Order);
        Assert.Equal(2, cleanupAttempts);
        Assert.False(Directory.Exists(Path.Combine(modRoot, "1"))); Assert.False(File.Exists(Path.Combine(modRoot, "1.mod")));
        Assert.False(Directory.Exists(cache));
        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(modRoot, "2", "kept.asset")));
        Assert.Equal("world stays", await File.ReadAllTextAsync(world));
        await workflow.ApplyBeforeStartAsync(id); Assert.Equal(2, cleanupAttempts);
    }

    [Fact]
    public async Task QueuedReplacementDoesNotWriteOrDeleteUntilNextStartup()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", root,
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }) { Mods = ["1", "2"] }));
        var writes = 0; var deletions = 0;
        var workflow = new ModOrderWorkflow(Repository, Path.Combine(root, "pending.json"),
            (_, _) => throw new InvalidOperationException("No stop expected"), (_, _) => throw new InvalidOperationException("No start expected"),
            (_, _, _) => { writes++; return Task.CompletedTask; }, removeFiles: (_, removed, _) =>
            { Assert.Equal(new[] { "1" }, removed); deletions++; return Task.CompletedTask; });
        var result = await workflow.SaveAsync(id, ["2", "3"], true, SaveChoice.SaveForNextStart, replace: true);
        Assert.True(result.Pending); Assert.Equal(0, writes); Assert.Equal(0, deletions);
        Assert.Equal(new[] { "1", "2" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
        await workflow.ApplyBeforeStartAsync(id); Assert.Equal(1, writes); Assert.Equal(1, deletions);
        Assert.Equal(new[] { "2", "3" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
    }

    [Fact]
    public async Task WriteCanReadCatalogAndStartupCanReenterPendingOrderWithoutDeadlock()
    {
        var id = await Seed();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ModOrderWorkflow workflow = null!;
        workflow = new(Repository, Path.Combine(root, "pending.json"),
            (_, _) => Task.FromResult(true),
            async (_, ct) => await workflow.ApplyBeforeStartAsync(id, ct),
            async (_, _, ct) => Assert.Single((await Repository.ReadAsync(ct)).Servers));
        var result = await workflow.SaveAsync(id, ["2", "1"], true, SaveChoice.SaveAndRestart, timeout.Token);
        Assert.True(result.Restarted);
        Assert.Equal(new[] { "2", "1" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-order-" + Guid.NewGuid().ToString("N"));
    private CatalogRepository Repository => new(Path.Combine(root, "catalog.json"));
    private async Task<Guid> Seed()
    {
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", "fixture", new()
        { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }) { Mods = ["1", "2"] }));
        return id;
    }
    private ModOrderWorkflow Workflow(Func<Guid, IReadOnlyList<string>, CancellationToken, Task> write,
        Func<IReadOnlyList<Guid>, CancellationToken, Task<bool>>? stop = null) => new(Repository,
        Path.Combine(root, "pending.json"), stop ?? ((_, _) => Task.FromResult(true)), (_, _) => Task.CompletedTask, write);

    [Fact]
    public async Task DamagedPendingDocumentIsRejectedBeforeStoppingServers()
    {
        var id = await Seed();
        await File.WriteAllTextAsync(Path.Combine(root, "pending.json"), "{\"SchemaVersion\":999,\"Changes\":[]}");
        var workflow = Workflow((_, _, _) => throw new InvalidOperationException("No write expected"),
            (_, _) => throw new InvalidOperationException("No stop expected"));
        await Assert.ThrowsAsync<InvalidDataException>(() => workflow.SaveAsync(id, ["2", "1"], true, SaveChoice.SaveAndRestart));
        Assert.Equal(new[] { "1", "2" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
        Assert.Contains("999", await File.ReadAllTextAsync(Path.Combine(root, "pending.json")));
    }

    [Fact]
    public async Task RestartTargetsOnlySelectedClusterMember()
    {
        var cluster = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        await Repository.ChangeAsync(c =>
        {
            var settings = new ServerSettings { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 };
            c.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, settings));
            c.AddServer(new(first, ArkEdition.ASE, "TheIsland", "first", settings, cluster) { Mods = ["1", "2"] });
            c.AddServer(new(second, ArkEdition.ASE, "ScorchedEarth_P", "second", settings with
                { GamePort = 7787, QueryPort = 27025, RconPort = 32340 }, cluster) { Mods = ["1", "2"] });
        });
        var events = new List<string>();
        var workflow = new ModOrderWorkflow(Repository, Path.Combine(root, "pending.json"),
            (ids, _) => { Assert.Equal(new[] { first }, ids); events.Add("stop"); return Task.FromResult(true); },
            (ids, _) => { Assert.Equal(new[] { first }, ids); events.Add("start"); return Task.CompletedTask; },
            (id, order, _) => { Assert.Equal(first, id); Assert.Equal(new[] { "2", "1" }, order); events.Add("write"); return Task.CompletedTask; });
        var result = await workflow.SaveAsync(first, ["2", "1"], true, SaveChoice.SaveAndRestart);
        Assert.True(result.Restarted);
        Assert.Equal(new[] { "stop", "write", "start" }, events);
        var servers = (await Repository.ReadAsync()).Servers;
        Assert.Equal(new[] { "2", "1" }, servers.Single(s => s.Id == first).Mods);
        Assert.Equal(new[] { "1", "2" }, servers.Single(s => s.Id == second).Mods);
    }

    [Fact]
    public async Task MembershipChangeRejectsStalePendingOrderBeforeWriting()
    {
        var id = await Seed(); var writes = 0;
        var workflow = Workflow((_, _, _) => { writes++; return Task.CompletedTask; });
        await workflow.SaveAsync(id, ["2", "1"], true, SaveChoice.SaveForNextStart);
        await Repository.ChangeAsync(c => c.RemoveMod(id, "1"));
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.ApplyBeforeStartAsync(id));
        Assert.Equal(0, writes);
        Assert.Equal(new[] { "2" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
        Assert.True(File.Exists(Path.Combine(root, "pending.json")));
    }

    [Fact]
    public async Task PendingOrderSurvivesReloadAndAppliesOnceBeforeStart()
    {
        var id = await Seed(); var writes = new List<string>();
        Task Write(Guid _, IReadOnlyList<string> order, CancellationToken token)
        { writes.Add(string.Join(",", order)); return Task.CompletedTask; }
        var input = new List<string> { "2", "1" };
        var saved = await Workflow(Write, (_, _) => throw new InvalidOperationException("No stop expected"))
            .SaveAsync(id, input, true, SaveChoice.SaveForNextStart);
        input.Clear();
        Assert.True(saved.Pending); Assert.Empty(writes);
        var queued = await Workflow(Write).ReadAsync(id);
        Assert.Equal(id, queued.ServerId); Assert.Equal(new[] { "2", "1" }, queued.Order);
        queued.Order!.Clear();
        Assert.Equal(new[] { "2", "1" }, (await Workflow(Write).ReadAsync(id)).Order);
        Assert.Equal(new[] { "1", "2" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
        await Workflow(Write).ApplyBeforeStartAsync(id);
        await Workflow(Write).ApplyBeforeStartAsync(id);
        Assert.Equal("2,1", Assert.Single(writes));
        Assert.Null((await Workflow(Write).ReadAsync(id)).Order);
        Assert.Equal(new[] { "2", "1" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
    }

    [Fact]
    public async Task FailedWriteRetainsPendingOrderAndLeavesCatalogUnchanged()
    {
        var id = await Seed();
        await Assert.ThrowsAsync<IOException>(() => Workflow((_, _, _) => throw new IOException("Fixture failure"))
            .SaveAsync(id, ["2", "1"], false, SaveChoice.SaveAndRestart));
        Assert.Equal(new[] { "1", "2" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
        await Workflow((_, _, _) => Task.CompletedTask).ApplyBeforeStartAsync(id);
        Assert.Equal(new[] { "2", "1" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
    }

    [Fact]
    public async Task CancelDeclinedStopAndInvalidMembershipNeverWriteOrSavePending()
    {
        var id = await Seed(); var stops = 0;
        var workflow = Workflow((_, _, _) => throw new InvalidOperationException("No write expected"),
            (_, _) => { stops++; return Task.FromResult(false); });
        Assert.False((await workflow.SaveAsync(id, ["2", "1"], true, SaveChoice.Cancel)).Saved);
        Assert.Equal(0, stops);
        Assert.False((await workflow.SaveAsync(id, ["2", "1"], true, SaveChoice.SaveAndRestart)).Saved);
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.SaveAsync(id, ["2", "2"], false, SaveChoice.SaveForNextStart));
        Assert.False(File.Exists(Path.Combine(root, "pending.json")));
        Assert.Equal(new[] { "1", "2" }, Assert.Single((await Repository.ReadAsync()).Servers).Mods);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
