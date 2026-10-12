using System.Security.Cryptography;
using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ClusterInheritanceRecoveryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FreshQueueRecoversValuesAndOverridePolicyTogetherWithoutChangingSibling(int interruptedPhase)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-inheritance-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json")); var cluster = Guid.NewGuid();
            var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "map"),
                new() { Name = "Selected", MaxPlayers = 40, GamePort = 7777, QueryPort = 27015, RconPort = 32330 }, cluster)
            { OverrideClusterMaxPlayers = true, ClusterOverrides = [new("GameUserSettings.ini", "ServerSettings", "TamingSpeedMultiplier")] };
            var peer = server with { Id = Guid.NewGuid(), Directory = Path.Combine(root, "peer"), Settings = server.Settings with
                { Name = "Peer", GamePort = 7787, QueryPort = 27016, RconPort = 32331 } };
            var shared = new ServerPreset("Shared", ArkEdition.ASE, server.Settings with { MaxPlayers = 70 }, [])
                { UserSettings = "[ServerSettings]\nTamingSpeedMultiplier=8\n", GameSettings = "" };
            await repository.ChangeAsync(c => { c.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, shared.Settings) { Defaults = shared }); c.AddServer(server); c.AddServer(peer); });
            var binary = Path.Combine(server.Directory, "ShooterGame", "Binaries", OperatingSystem.IsWindows() ? "Win64" : "Linux",
                OperatingSystem.IsWindows() ? "ShooterGameServer.exe" : "ShooterGameServer");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!); await File.WriteAllTextAsync(binary, "never launched");
            var folder = Path.Combine(server.Directory, "ShooterGame", "Saved", "Config", OperatingSystem.IsWindows() ? "WindowsServer" : "LinuxServer");
            Directory.CreateDirectory(folder); var user = Path.Combine(folder, "GameUserSettings.ini"); var game = Path.Combine(folder, "Game.ini");
            const string original = "[ServerSettings]\nServerAdminPassword=private-fixture\nMaxPlayers=40\nTamingSpeedMultiplier=5\n";
            await File.WriteAllTextAsync(user, original); await File.WriteAllTextAsync(game, "[Other]\nOption=keep\n");
            var catalog = await repository.ReadAsync(); var sibling = JsonSerializer.Serialize(catalog.Servers.Single(s => s.Id == peer.Id));
            var token = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(catalog.Clusters.Single())));
            var expected = ClusterOverrideManagerOperations.Review(server) with { SharedSettingsToken = token };
            var reset = ClusterInheritance.BuildReset(server, new(expected, [], false, false, true), shared, "", "");
            var preparation = await new PresetApplicationReviewService(repository, new PresetLibrary(Path.Combine(root, "library.json")))
                .PrepareAsync(new(server.Id, Guid.NewGuid()) { DraftPreset = reset.Preset, Removals = reset.Removals,
                    OverridePolicy = new([], false, false), SharedDefaultsToken = token });
            var queueFile = Path.Combine(root, "pending.json");
            PresetApplicationQueue Queue() => new(new CatalogRepository(Path.Combine(root, "catalog.json")), queueFile,
                (_, _) => Task.CompletedTask, (_, _, _) => throw new InvalidOperationException("No mods were removed."));
            await Queue().EnqueueAsync(preparation);
            Assert.Equal(original, await File.ReadAllTextAsync(user));
            Assert.True((await repository.ReadAsync()).Servers.Single(s => s.Id == server.Id).OverrideClusterMaxPlayers);
            if (interruptedPhase > 0)
            {
                var store = new AtomicJsonStore<PendingPresetApplications>(queueFile, true); var document = (await store.LoadAsync())!;
                await store.SaveAsync(document with { Changes = document.Changes.Select(p => p with { Started = true }).ToList() });
                await File.WriteAllTextAsync(user, preparation.Plan.UserSettings); await File.WriteAllTextAsync(game, preparation.Plan.GameSettings);
                if (interruptedPhase == 2) await repository.ChangeAsync(c =>
                {
                    c.UpdateSettings(server.Id, preparation.Plan.Settings); c.ReplaceMods(server.Id, preparation.Plan.Mods);
                    c.SetClusterOverrides(server.Id, [], false, false);
                });
            }
            await Queue().ApplyBeforeStartAsync(server.Id); await Queue().ApplyBeforeStartAsync(server.Id);
            var complete = await repository.ReadAsync(); var selected = complete.Servers.Single(s => s.Id == server.Id);
            Assert.Equal(70, selected.Settings.MaxPlayers); Assert.False(selected.OverrideClusterMaxPlayers); Assert.Empty(selected.ClusterOverrides);
            Assert.Equal(["8"], new IniDocument(await File.ReadAllTextAsync(user)).GetValues("ServerSettings", "TamingSpeedMultiplier"));
            Assert.Contains("ServerAdminPassword=private-fixture", await File.ReadAllTextAsync(user));
            Assert.Null(await Queue().ReadAsync(server.Id)); Assert.Equal(sibling, JsonSerializer.Serialize(complete.Servers.Single(s => s.Id == peer.Id)));
        }
        finally { Directory.Delete(root, true); }
    }
}
