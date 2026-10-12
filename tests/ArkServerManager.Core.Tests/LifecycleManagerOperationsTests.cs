using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class LifecycleManagerOperationsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-lifecycle-request-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReconnectUsesSameJobAndClientCancellationDoesNotCancelStartup()
    {
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var id = Guid.NewGuid();
        await repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", root,
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 })));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime();
        var coordinator = new ServerLifecycleCoordinator(repository, _ => runtime,
            async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        await using var jobs = new BackgroundOperations(8, 1);
        var manager = new LifecycleManagerOperations(coordinator, jobs);
        using var clientLifetime = new CancellationTokenSource();
        var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "lifecycle-execute",
            JsonSerializer.SerializeToElement(new LifecycleRequest(id, LifecycleAction.Start)));
        var submitted = Assert.IsType<BackgroundOperation>(await manager.HandleAsync(request, clientLifetime.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            clientLifetime.Cancel();
            var reconnected = Assert.IsType<BackgroundOperation>(await manager.HandleAsync(request));
            Assert.Equal(submitted.Id, reconnected.Id);
            Assert.Equal(submitted.CreatedAt, reconnected.CreatedAt);
        }
        finally { release.TrySetResult(); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (jobs.Find(request.Id)!.FinishedAt is null) await Task.Delay(5, timeout.Token);
        var completed = jobs.Find(request.Id)!;
        Assert.Equal(BackgroundOperationState.Succeeded, completed.State);
        Assert.True(completed.Result!.Value.Deserialize<LifecycleOperationResult>()!.Success);
        Assert.Equal(1, runtime.Starts);
    }

    [Fact]
    public async Task MemberStopLeavesRunningClusterSiblingUntouchedAndCannotReplayAsClusterStop()
    {
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var cluster = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        await repository.ChangeAsync(c =>
        {
            c.AddCluster(new(cluster, "Fixture cluster", ArkEdition.ASE, new() { Name = "Fixture cluster" }));
            c.AddServer(new(first, ArkEdition.ASE, "TheIsland", Path.Combine(root, "first"),
                new() { Name = "First", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }, cluster));
            c.AddServer(new(second, ArkEdition.ASE, "Ragnarok", Path.Combine(root, "second"),
                new() { Name = "Second", GamePort = 7779, QueryPort = 27017, RconPort = 32332 }, cluster));
        });
        var runtimes = new Dictionary<Guid, Runtime> { [first] = new(), [second] = new() };
        var coordinator = new ServerLifecycleCoordinator(repository, id => runtimes[id], (_, _) => Task.CompletedTask);
        Assert.True((await coordinator.ExecuteAsync(first, LifecycleAction.Start)).Success);
        await using var jobs = new BackgroundOperations(8, 1);
        var manager = new LifecycleManagerOperations(coordinator, jobs);
        var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "lifecycle-execute-member",
            JsonSerializer.SerializeToElement(new LifecycleRequest(first, LifecycleAction.Stop)));
        var submitted = Assert.IsType<BackgroundOperation>(await manager.HandleAsync(request));
        Assert.Equal("server-member-lifecycle", submitted.Kind);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (jobs.Find(request.Id)!.FinishedAt is null) await Task.Delay(5, timeout.Token);
        var result = jobs.Find(request.Id)!.Result!.Value.Deserialize<LifecycleOperationResult>()!;
        Assert.True(result.Success); Assert.Single(result.Members);
        Assert.Equal(first, result.Members[0].ServerId);
        Assert.Equal(ServerState.Stopped, runtimes[first].State);
        Assert.Equal(ServerState.Running, runtimes[second].State);
        await Assert.ThrowsAsync<ArgumentException>(() => manager.HandleAsync(request with { Operation = "lifecycle-execute" }));
        Assert.Equal(ServerState.Running, runtimes[second].State);
    }

    private sealed class Runtime : IServerRuntime
    {
        public ServerState State { get; private set; } = ServerState.Stopped;
        public int Starts { get; private set; }
        public Task StartAsync(ServerDefinition definition, CancellationToken ct)
        { Starts++; State = ServerState.Running; return Task.CompletedTask; }
        public Task<bool> StopAsync(CancellationToken ct)
        { State = ServerState.Stopped; return Task.FromResult(true); }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
