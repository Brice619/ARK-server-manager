using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class LifecycleStatusDuringStartupTests
{
    [Fact]
    public async Task DisplayStatusRemainsAvailableWhileNativeStartupOwnsTheLifecycleScope()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-starting-status-" + Guid.NewGuid().ToString("N"));
        var runtime = new StartingRuntime();
        try
        {
            var id = Guid.NewGuid();
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            await repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", root,
                new() { Name = "Starting", GamePort = 7777, QueryPort = 27015, RconPort = 32330 })));
            var coordinator = new ServerLifecycleCoordinator(repository, _ => runtime, (_, _) => Task.CompletedTask);
            var startup = coordinator.ExecuteMemberAsync(id, LifecycleAction.Start);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await runtime.Entered.Task.WaitAsync(deadline.Token);
            try
            {
                var status = Assert.Single(await coordinator.StatusAsync(deadline.Token));
                Assert.True(status.Managed); Assert.Equal(ServerState.Starting, status.State);
                Assert.False(startup.IsCompleted);
            }
            finally { runtime.Complete.TrySetResult(); }
            Assert.True((await startup).Success);
            Assert.Equal(ServerState.Running, Assert.Single(await coordinator.StatusAsync()).State);
        }
        finally { runtime.Complete.TrySetResult(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class StartingRuntime : IServerRuntime
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ServerState State { get; private set; } = ServerState.Stopped;
        public async Task StartAsync(ServerDefinition definition, CancellationToken ct)
        { State = ServerState.Starting; Entered.TrySetResult(); await Complete.Task.WaitAsync(ct); State = ServerState.Running; }
        public Task<bool> StopAsync(CancellationToken ct) { State = ServerState.Stopped; return Task.FromResult(true); }
    }
}
