using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class DeletedServerQueueCleanupTests
{
    [Fact]
    public async Task CompletedDeletionRemovesOnlySelectedQueuesAndRefusesCleanupBeforeCatalogAndFilesAreGone()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-delete-queues-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var maps = Enumerable.Range(0, 2).Select(index => new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland",
                Path.Combine(root, "Servers", "map-" + index), new() { Name = "Map " + index, GamePort = 7777 + index * 10,
                    QueryPort = 27015 + index, RconPort = 32330 + index })).ToArray();
            await repository.ChangeAsync(c => { foreach (var map in maps) c.AddServer(map); });
            foreach (var map in maps)
            {
                var binary = Path.Combine(map.Directory, "ShooterGame", "Binaries", OperatingSystem.IsWindows() ? "Win64" : "Linux",
                    OperatingSystem.IsWindows() ? "ShooterGameServer.exe" : "ShooterGameServer");
                Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
                await File.WriteAllTextAsync(binary, "never launched");
                var config = Path.Combine(map.Directory, "ShooterGame", "Saved", "Config", OperatingSystem.IsWindows() ? "WindowsServer" : "LinuxServer");
                Directory.CreateDirectory(config);
                await File.WriteAllTextAsync(Path.Combine(config, "GameUserSettings.ini"), "[ServerSettings]\nServerAdminPassword=fixture-admin\n");
            }
            string FilePath(string name) => Path.Combine(root, "app-settings", name + ".json");
            Task NoEffect(CancellationToken _) => throw new InvalidOperationException("Queue cleanup must not touch server files or processes.");
            var settings = new SettingsChangeWorkflow(repository, FilePath("settings"), (_, _) => throw new InvalidOperationException(),
                (_, ct) => NoEffect(ct), (_, _, ct) => NoEffect(ct));
            var mods = new ModOrderWorkflow(repository, FilePath("mods"), (_, _) => throw new InvalidOperationException(),
                (_, ct) => NoEffect(ct), (_, _, ct) => NoEffect(ct));
            var access = new ServerAccessChanges(repository, FilePath("access"), (_, ct) => NoEffect(ct));
            var advanced = new PendingConfigurationEdits(repository, FilePath("advanced"), (_, ct) => NoEffect(ct));
            var presets = new PresetApplicationQueue(repository, FilePath("presets"), (_, ct) => NoEffect(ct), (_, _, ct) => NoEffect(ct));
            var updates = new ServerUpdateWorkflow(FilePath("updates"), (_, _) => throw new InvalidOperationException(),
                (_, _) => throw new InvalidOperationException(), (_, _, _) => throw new InvalidOperationException(), (_, ct) => NoEffect(ct));
            var schedules = new DailyRestartSchedules(repository, FilePath("schedules"));
            var library = new PresetLibrary(FilePath("library"));
            var preset = await library.SaveAsync(new("Fixture", ArkEdition.ASE, maps[0].Settings, []));
            var reviews = new PresetApplicationReviewService(repository, library);
            foreach (var map in maps)
            {
                Assert.True((await settings.SaveAsync(map.Id, map.Settings with { MaxPlayers = 40 }, true, SaveChoice.SaveForNextStart)).Pending);
                Assert.True((await mods.SaveAsync(map.Id, [], true, SaveChoice.SaveForNextStart)).Pending);
                await access.QueueAsync(new(await access.ReadAsync(map.Id), null, "queued-private-join"));
                await presets.EnqueueAsync(await reviews.PrepareAsync(new(map.Id, preset.Id)));
                var source = await ServerConfigurationReader.ReadAsync(repository, new(map.Id, "GameUserSettings.ini"));
                await advanced.EnqueueAsync(Guid.NewGuid(), new(map.Id, "GameUserSettings.ini", source.SourceSHA256!, source.Text + "\n[Fixture]\nValue=advanced\n"));
                await schedules.SaveAsync(map.Id, new(4, 0), true, true);
            }
            await new AtomicJsonStore<UpdateQueueDocument>(FilePath("updates")).SaveAsync(new(1,
                maps.Select(map => new QueuedServerUpdate(map.Id, 123, DateTimeOffset.UtcNow) { Directory = map.Directory, Edition = map.Edition }).ToList()));
            var operation = Guid.NewGuid();
            var deletion = new ServerDeletionRecord(1, operation, maps[0].Id, maps[0].Settings.Name, maps[0].Directory,
                Path.Combine(root, "Servers", ".ark-delete-" + operation.ToString("N")), null, [], ServerDeletionPhase.FilesRemoved);
            Func<Task>[] cleanup = [() => settings.RemoveDeletedAsync(deletion), () => mods.RemoveDeletedAsync(deletion),
                () => access.RemoveDeletedAsync(deletion), () => presets.RemoveDeletedAsync(deletion),
                () => updates.RemoveDeletedAsync(repository, deletion), () => schedules.RemoveDeletedAsync(deletion), () => advanced.RemoveDeletedAsync(deletion)];
            var before = new Dictionary<string, string>();
            foreach (var name in new[] { "settings", "mods", "access", "presets", "updates", "schedules", "advanced" }) before[name] = await File.ReadAllTextAsync(FilePath(name));
            foreach (var remove in cleanup) await Assert.ThrowsAsync<InvalidOperationException>(remove);
            await repository.ChangeAsync(c => c.RemoveServer(maps[0].Id));
            foreach (var remove in cleanup) await Assert.ThrowsAsync<InvalidOperationException>(remove);
            foreach (var item in before) Assert.Equal(item.Value, await File.ReadAllTextAsync(FilePath(item.Key)));
            Directory.Delete(maps[0].Directory, true);
            foreach (var remove in cleanup) { await remove(); await remove(); }
            Assert.Equal(maps[1].Id, Assert.Single((await new AtomicJsonStore<PendingSettingsDocument>(FilePath("settings")).LoadAsync())!.Changes).ServerId);
            Assert.Equal(maps[1].Id, Assert.Single((await new AtomicJsonStore<PendingModOrders>(FilePath("mods")).LoadAsync())!.Changes).ServerId);
            Assert.Equal(maps[1].Id, Assert.Single((await new AtomicJsonStore<PendingServerAccessDocument>(FilePath("access")).LoadAsync())!.Changes).ServerId);
            Assert.Equal(maps[1].Id, Assert.Single((await new AtomicJsonStore<PendingPresetApplications>(FilePath("presets")).LoadAsync())!.Changes).Application.Server.Id);
            Assert.Equal(maps[1].Id, Assert.Single((await new AtomicJsonStore<UpdateQueueDocument>(FilePath("updates")).LoadAsync())!.Updates).ServerId);
            Assert.Equal(maps[1].Id, Assert.Single((await new AtomicJsonStore<DailyRestartDocument>(FilePath("schedules")).LoadAsync())!.Schedules).ServerId);
            Assert.Equal(maps[1].Id, Assert.Single((await new AtomicJsonStore<PendingConfigurationDocument>(FilePath("advanced")).LoadAsync())!.Changes).Server.Id);
            Assert.True(Directory.Exists(maps[1].Directory));
        }
        finally { Directory.Delete(root, true); }
    }
}
