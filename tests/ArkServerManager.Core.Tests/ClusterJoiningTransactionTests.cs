using System.Text;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ClusterJoiningTransactionTests
{
    [Theory]
    [InlineData("accepted", false)]
    [InlineData("queued", false)]
    [InlineData("membership", false)]
    [InlineData("member-applied", false)]
    [InlineData("member-applied", true)]
    public async Task FreshServicesFinishInterruptedBatchWithoutChangingIdentityPasswordsOrUnrelatedServer(string phase, bool existingCluster)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-joining-transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var servers = new List<ServerDefinition>();
            for (var index = 0; index < 3; index++)
            {
                var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "map-" + index),
                    new() { Name = "Personal " + index, MaxPlayers = 30 + index, GamePort = 7777 + index * 10,
                        QueryPort = 27015 + index * 10, RconPort = 32330 + index * 10 }) { Mods = ["123"] };
                var binary = Path.Combine(server.Directory, "ShooterGame", "Binaries", OperatingSystem.IsWindows() ? "Win64" : "Linux",
                    OperatingSystem.IsWindows() ? "ShooterGameServer.exe" : "ShooterGameServer");
                Directory.CreateDirectory(Path.GetDirectoryName(binary)!); await File.WriteAllTextAsync(binary, "fixture; never launched");
                var config = Path.Combine(server.Directory, "ShooterGame", "Saved", "Config", OperatingSystem.IsWindows() ? "WindowsServer" : "LinuxServer");
                Directory.CreateDirectory(config);
                await File.WriteAllTextAsync(Path.Combine(config, "GameUserSettings.ini"),
                    $"[ServerSettings]\nServerAdminPassword=admin-{index}\nServerPassword=join-{index}\nRCONEnabled=True\nRCONPort={server.Settings.RconPort}\nTamingSpeedMultiplier=2\n",
                    index == 0 ? Encoding.Unicode : new UTF8Encoding(false));
                servers.Add(server);
            }
            var target = new ClusterDefinition(Guid.NewGuid(), "Target", ArkEdition.ASE, servers[0].Settings with { Name = "Target", MaxPlayers = 80 })
            { Defaults = new("Shared", ArkEdition.ASE, servers[0].Settings with { MaxPlayers = 80 }, ["456"])
                { UserSettings = "[ServerSettings]\nTamingSpeedMultiplier=8\n" } };
            await repository.ChangeAsync(c => { foreach (var server in servers) c.AddServer(server); if (existingCluster) c.AddCluster(target); });
            var unrelatedFile = Config(servers[2]); var unrelatedBytes = await File.ReadAllBytesAsync(unrelatedFile);
            var removed = new List<Guid>(); var interrupted = false;
            var transaction = Create(stage => { if (!interrupted && stage == phase) { interrupted = true; throw new IOException("Simulated interruption"); } });
            var intent = await transaction.PrepareAsync(Guid.NewGuid(), servers.Take(2).ToArray(), target, !existingCluster, CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() => transaction.ExecuteAsync(intent, CancellationToken.None));
            Assert.Single(await new ClusterJoiningJournal(root).ReadAsync());
            await Create(null).ResumeForServerAsync(servers[0].Id, CancellationToken.None);
            await Create(null).ResumeForServerAsync(servers[0].Id, CancellationToken.None);
            Assert.Empty(await new ClusterJoiningJournal(root).ReadAsync());
            var result = await repository.ReadAsync();
            foreach (var original in servers.Take(2))
            {
                var actual = result.Servers.Single(s => s.Id == original.Id);
                Assert.Equal(target.Id, actual.ClusterId); Assert.Equal(80, actual.Settings.MaxPlayers);
                Assert.Equal(original.Settings.Name, actual.Settings.Name); Assert.Equal(original.Settings.GamePort, actual.Settings.GamePort);
                Assert.Equal(original.Settings.RconPort, actual.Settings.RconPort); Assert.Equal(new[] { "456" }, actual.Mods);
                var user = await File.ReadAllTextAsync(Config(original));
                Assert.Contains("TamingSpeedMultiplier=8", user); Assert.Contains("ServerAdminPassword=admin-" + servers.IndexOf(original), user);
                Assert.Contains("ServerPassword=join-" + servers.IndexOf(original), user);
                Assert.False(actual.OverrideClusterMaxPlayers); Assert.False(actual.OverrideClusterMods);
                Assert.Empty(actual.ClusterOverrides);
            }
            Assert.Equal(unrelatedBytes, await File.ReadAllBytesAsync(unrelatedFile));
            Assert.Null(result.Servers.Single(s => s.Id == servers[2].Id).ClusterId);
            Assert.Equal(new byte[] { 0xff, 0xfe }, (await File.ReadAllBytesAsync(Config(servers[0]))).Take(2));
            Assert.Equal(servers.Take(2).Select(s => s.Id).Order(), removed.Order());
            // A retained batch record loses only a deliberately deleted member; peers can still finish.
            var cleanupJournal = new ClusterJoiningJournal(root);
            await cleanupJournal.AcceptAsync(intent);
            var deletion = new ServerDeletionRecord(1, Guid.NewGuid(), servers[0].Id, servers[0].Settings.Name,
                servers[0].Directory, Path.Combine(root, "staged-deletion"), target.Id, [], ServerDeletionPhase.FilesRemoved);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await cleanupJournal.RemoveDeletedAsync(await repository.ReadAsync(), deletion));
            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(servers[0].Directory));
            Directory.Delete(servers[0].Directory, true);
            await repository.ChangeAsync(c => c.RemoveServer(servers[0].Id));
            await cleanupJournal.RemoveDeletedAsync(await repository.ReadAsync(), deletion);
            await cleanupJournal.RemoveDeletedAsync(await repository.ReadAsync(), deletion);
            var remaining = Assert.Single(await cleanupJournal.ReadAsync());
            Assert.Equal(servers[1].Id, Assert.Single(remaining.Originals).Id);
            Assert.DoesNotContain("admin-0", System.Text.Json.JsonSerializer.Serialize(remaining));
            await Create(null).ResumeForServerAsync(servers[1].Id, CancellationToken.None);
            Assert.Empty(await cleanupJournal.ReadAsync());
            Assert.Equal(unrelatedBytes, await File.ReadAllBytesAsync(unrelatedFile));

            ClusterJoiningTransaction Create(Action<string>? checkpoint)
            {
                var queue = new PresetApplicationQueue(repository, Path.Combine(root, "pending-presets.json"), (_, _) => Task.CompletedTask,
                    (server, mods, _) => { Assert.Equal(new[] { "123" }, mods); removed.Add(server.Id); return Task.CompletedTask; });
                return new(repository, new(root), new(repository, new PresetLibrary(Path.Combine(root, "presets.json"))), queue,
                    (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, checkpoint);
            }
            string Config(ServerDefinition server) => Path.Combine(server.Directory, "ShooterGame", "Saved", "Config",
                OperatingSystem.IsWindows() ? "WindowsServer" : "LinuxServer", "GameUserSettings.ini");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
