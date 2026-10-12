using System.Text.Json;
using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class ClusterPresetTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public async Task BatchPreflightsStaleReviewsAndReportsMapFailuresSeparately(bool stale, bool directEdit, bool changedDefaults)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-cluster-preset-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json")); var cluster = Guid.NewGuid();
            var maps = Enumerable.Range(0, 3).Select(index => new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "map" + index),
                new() { Name = "Map " + index, GamePort = 7777 + index * 10, QueryPort = 27015 + index * 10, RconPort = 32330 + index * 10 }, cluster)).ToList();
            maps[0] = maps[0] with { OverrideClusterMaxPlayers = true };
            await repository.ChangeAsync(catalog => { catalog.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, maps[0].Settings)); foreach (var map in maps) catalog.AddServer(map); });
            var library = new PresetLibrary(Path.Combine(root, "presets.json"));
            var preset = await library.SaveAsync(new ServerPreset("Shared", ArkEdition.ASE, maps[0].Settings with { MaxPlayers = 40 }, []));
            var reviews = new PresetApplicationReviewService(repository, library, (id, name, _) => Task.FromResult(new ServerConfigurationFile(id, name, "WindowsServer",
                name == "Game.ini" ? "" : "[ServerSettings]\nMaxPlayers=70\n", []) { SourceSHA256 = new string('A', 64) }));
            var queue = new PresetApplicationQueue(repository, Path.Combine(root, "pending.json"), (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
            await using var jobs = new BackgroundOperations(16, 1);
            var applications = new PresetApplicationManagerOperations(reviews, queue, jobs,
                (work, ct) => work(maps.Select(map => new MemberOperationResult(map.Id, ServerState.Running, true, null) { Managed = true }).ToArray(), ct),
                (id, _) => id == maps[1].Id ? Task.FromException(new InvalidOperationException("Conflicting map change")) : Task.CompletedTask,
                (_, _, _) => throw new InvalidOperationException("Next-start saves must not restart."));
            var manager = new ClusterPresetManagerOperations(repository, reviews, applications, jobs);
            var selection = new ClusterPresetSelection(cluster, directEdit ? Guid.NewGuid() : preset.Id, maps.Select(map => map.Id).ToList())
            { DraftPreset = directEdit ? preset.Preset with { Name = "Direct shared settings", UserSettings = "[ServerSettings]\nHarvestAmountMultiplier=8\n" } : null };
            var review = await manager.ReviewAsync(selection);
            Assert.Equal(70, review.Maps.Single(map => map.ServerId == maps[0].Id).DesiredMaxPlayers);
            Assert.Equal(40, review.Maps.Single(map => map.ServerId == maps[2].Id).DesiredMaxPlayers);
            if (stale) await repository.ChangeAsync(catalog => catalog.UpdateSettings(maps[2].Id, maps[2].Settings with { MaxPlayers = 80 }));
            if (changedDefaults) await repository.ChangeAsync(catalog => catalog.UpdateClusterDefaults(cluster, "Changed defaults", 90));
            var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "cluster-preset-save-start",
                JsonSerializer.SerializeToElement(new ClusterPresetSave(review, SaveChoice.SaveForNextStart, true) { UseForNewMaps = true }));
            manager.Start(request); manager.Start(request);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (jobs.Find(request.Id)!.FinishedAt is null) await Task.Delay(10, deadline.Token);
            var result = jobs.Find(request.Id)!;
            if (stale || changedDefaults)
            {
                Assert.Equal(BackgroundOperationState.Failed, result.State);
                foreach (var map in maps) Assert.Null(await queue.ReadAsync(map.Id));
                Assert.Null(Assert.Single((await repository.ReadAsync()).Clusters).Defaults);
            }
            else
            {
                Assert.Equal(BackgroundOperationState.Succeeded, result.State);
                var batch = result.Result!.Value.Deserialize<ClusterPresetSaveResult>()!;
                Assert.Equal(3, batch.Maps.Count); Assert.Single(batch.Maps, map => map.Error is not null);
                Assert.True(batch.DefaultsSaved);
                var defaults = Assert.Single((await repository.ReadAsync()).Clusters).Defaults;
                Assert.NotNull(defaults); Assert.Equal(40, defaults.Settings.MaxPlayers);
                Assert.Contains("MaxPlayers=40", defaults.UserSettings);
                if (directEdit) Assert.Contains("HarvestAmountMultiplier=8", defaults.UserSettings);
                Assert.Single(await library.ListAsync());
                Assert.NotNull(await queue.ReadAsync(maps[0].Id)); Assert.Null(await queue.ReadAsync(maps[1].Id)); Assert.NotNull(await queue.ReadAsync(maps[2].Id));
                Assert.All((await repository.ReadAsync()).Servers, server => Assert.Equal(70, server.Settings.MaxPlayers));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
