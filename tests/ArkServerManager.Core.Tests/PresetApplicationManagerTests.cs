using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class PresetApplicationManagerTests
{
    [Theory]
    [InlineData(SaveChoice.SaveForNextStart, false)]
    [InlineData(SaveChoice.SaveAndRestart, false)]
    [InlineData(SaveChoice.SaveAndRestart, true)]
    public async Task RunningMapSavesDurablyAndRestartsOnlyItsOwnRuntime(SaveChoice choice, bool stopFails)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-preset-manager-" + Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Path.Combine(root, "selected");
            var binary = Path.Combine(directory, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!); await File.WriteAllTextAsync(binary, "fixture");
            var config = Path.Combine(directory, "ShooterGame", "Saved", "Config", "WindowsServer");
            Directory.CreateDirectory(config);
            var userPath = Path.Combine(config, "GameUserSettings.ini");
            await File.WriteAllTextAsync(userPath, "[ServerSettings]\nServerAdminPassword=private\nRCONEnabled=True\n");
            var original = await File.ReadAllTextAsync(userPath);
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var clusterId = Guid.NewGuid(); var selectedId = Guid.NewGuid(); var siblingId = Guid.NewGuid();
            var settings = new ServerSettings { Name = "Selected", MaxPlayers = 20, GamePort = 7777, QueryPort = 27015, RconPort = 32330 };
            await repository.ChangeAsync(c =>
            {
                c.AddCluster(new(clusterId, "Cluster", ArkEdition.ASE, settings));
                c.AddServer(new(selectedId, ArkEdition.ASE, "TheIsland", directory, settings, clusterId));
                c.AddServer(new(siblingId, ArkEdition.ASE, "Ragnarok", Path.Combine(root, "sibling"),
                    settings with { Name = "Sibling", GamePort = 7780, QueryPort = 27016, RconPort = 32331 }, clusterId));
            });
            var library = new PresetLibrary(Path.Combine(root, "presets.json"));
            var preset = await library.SaveAsync(new("Rates", ArkEdition.ASE, settings with { MaxPlayers = 40 }, []));
            var review = new PresetApplicationReviewService(repository, library);
            var runtimes = new Dictionary<Guid, Runtime> { [selectedId] = new(), [siblingId] = new() };
            var queue = new PresetApplicationQueue(repository, Path.Combine(root, "pending.json"), (server, ct) =>
            {
                Assert.Equal(ServerState.Stopped, runtimes[server.Id].State);
                return Task.CompletedTask;
            }, (server, removed, ct) => throw new InvalidOperationException("No mods should be removed."));
            using var gate = new SemaphoreSlim(1, 1);
            var coordinator = new ServerLifecycleCoordinator(repository, id => runtimes[id], queue.ApplyBeforeStartAsync, gate);
            Assert.True((await coordinator.ExecuteAsync(selectedId, LifecycleAction.Start)).Success);
            runtimes[selectedId].StopFails = stopFails;
            await using var jobs = new BackgroundOperations(8, 1);
            var manager = new PresetApplicationManagerOperations(review, queue, jobs, coordinator.WithStatusScopeAsync,
                (id, ct) => Task.CompletedTask, coordinator.ExecuteMemberAsync);
            var expected = (await review.PrepareAsync(new(selectedId, preset.Id))).Review;
            var requestId = Guid.NewGuid();
            manager.Start(new(1, requestId, "presets-application-save-start", JsonSerializer.SerializeToElement(new PresetApplicationSave(expected, choice, true))));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            BackgroundOperation completed;
            while (true)
            {
                completed = jobs.Find(requestId)!;
                if (completed.State is BackgroundOperationState.Succeeded or BackgroundOperationState.Failed or BackgroundOperationState.Canceled) break;
                await Task.Delay(20, deadline.Token);
            }
            Assert.Equal(BackgroundOperationState.Succeeded, completed.State);
            var result = completed.Result!.Value.Deserialize<PresetApplicationSaveResult>()!;
            Assert.Equal(1, runtimes[siblingId].Starts); Assert.Equal(0, runtimes[siblingId].Stops);
            Assert.Equal(ServerState.Running, runtimes[siblingId].State);
            if (choice == SaveChoice.SaveForNextStart || stopFails)
            {
                Assert.True(result.Save.Pending); Assert.False(result.Save.Restarted);
                Assert.Equal(original, await File.ReadAllTextAsync(userPath));
                Assert.Equal(20, (await repository.ReadAsync()).Servers.Single(s => s.Id == selectedId).Settings.MaxPlayers);
                Assert.NotNull(await queue.ReadAsync(selectedId));
                runtimes[selectedId].StopFails = false;
                Assert.True((await coordinator.ExecuteMemberAsync(selectedId, LifecycleAction.Restart)).Success);
            }
            else { Assert.False(result.Save.Pending); Assert.True(result.Save.Restarted); }
            Assert.Equal(2, runtimes[selectedId].Starts);
            Assert.Equal(40, runtimes[selectedId].LastDefinition!.Settings.MaxPlayers);
            Assert.Equal(20, runtimes[siblingId].LastDefinition!.Settings.MaxPlayers);
            Assert.Null(await queue.ReadAsync(selectedId));
            Assert.Equal(1, runtimes[siblingId].Starts); Assert.Equal(0, runtimes[siblingId].Stops);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Runtime : IServerRuntime
    {
        public ServerState State { get; private set; } = ServerState.Stopped;
        public bool StopFails; public int Starts; public int Stops; public ServerDefinition? LastDefinition;
        public Task StartAsync(ServerDefinition definition, CancellationToken ct)
        { LastDefinition = definition; Starts++; State = ServerState.Running; return Task.CompletedTask; }
        public Task<bool> StopAsync(CancellationToken ct)
        { Stops++; if (StopFails) return Task.FromResult(false); State = ServerState.Stopped; return Task.FromResult(true); }
    }
}
