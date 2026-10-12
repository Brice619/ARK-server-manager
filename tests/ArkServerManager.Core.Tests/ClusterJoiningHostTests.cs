using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ClusterJoiningHostTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ActualWindowsHostJoinsStandaloneServersAndAppliesSharedDefaults(bool createCluster, bool recoverAccepted)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ark-joining-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var servers = new List<ServerDefinition>();
            for (var index = 0; index < 2; index++)
            {
                var directory = Path.Combine(root, "map-" + index);
                var binary = Path.Combine(directory, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(binary)!); await File.WriteAllTextAsync(binary, "fixture; never launched");
                var config = Path.Combine(directory, "ShooterGame", "Saved", "Config", "WindowsServer"); Directory.CreateDirectory(config);
                await File.WriteAllTextAsync(Path.Combine(config, "GameUserSettings.ini"),
                    $"[ServerSettings]\nServerAdminPassword=admin-{index}\nServerPassword=join-{index}\nRCONEnabled=True\nRCONPort={32330 + index * 10}\nTamingSpeedMultiplier={2 + index}\n");
                servers.Add(new(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", directory,
                    new() { Name = "Personal " + index, MaxPlayers = 30 + index, GamePort = 7777 + index * 10,
                        QueryPort = 27015 + index * 10, RconPort = 32330 + index * 10 }));
            }
            var cluster = new ClusterDefinition(Guid.NewGuid(), "Target", ArkEdition.ASE, servers[0].Settings with { Name = "Target", MaxPlayers = 80 })
            { Defaults = new("Shared", ArkEdition.ASE, servers[0].Settings with { MaxPlayers = 80 }, [])
                { UserSettings = "[ServerSettings]\nTamingSpeedMultiplier=8\n" } };
            await repository.ChangeAsync(c => { foreach (var server in servers) c.AddServer(server); if (!createCluster) c.AddCluster(cluster); });
            ClusterJoiningRecord? accepted = null;
            if (recoverAccepted)
            {
                var target = createCluster ? new ClusterDefinition(cluster.Id, "Created target", ArkEdition.ASE, servers[0].Settings with { Name = "Created target" })
                { Defaults = PresetLibrary.WithoutPasswords(await PresetCapture.ReadAsync(repository, new(servers[0], "Created target"), CancellationToken.None)) } : cluster;
                var queue = new PresetApplicationQueue(repository, Path.Combine(root, "app-settings", "pending-presets.json"), (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
                var prepare = new ClusterJoiningTransaction(repository, new(root), new(repository, new PresetLibrary(Path.Combine(root, "presets.json"))), queue,
                    (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
                accepted = await prepare.PrepareAsync(Guid.NewGuid(), servers, target, createCluster, CancellationToken.None);
                await new ClusterJoiningJournal(root).AcceptAsync(accepted);
            }
            var hostDirectory = Path.Combine(AppContext.BaseDirectory, "host-fixture");
            await using var host = ManagedProcess.Start(new(Path.Combine(hostDirectory, "ArkServerManager.Host.exe"), hostDirectory,
                ["--data-root", root], Path.Combine(root, "host.log")));
            var client = new LocalManagerClient(root); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                try { await client.CallAsync<JsonElement>("ping", null, TimeSpan.FromMilliseconds(300), deadline.Token); break; }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException && !deadline.IsCancellationRequested)
                { if (!host.IsAlive) throw new IOException("Fixture host exited."); await Task.Delay(50, deadline.Token); }
            }
            var expected = await client.CallAsync<List<ClusterMemberReview>>("cluster-membership-read", null, TimeSpan.FromSeconds(5), deadline.Token);
            var id = Guid.NewGuid();
            var status = await client.CallAsync<List<ClusterJoiningStatus>>("cluster-joining-read", null, TimeSpan.FromSeconds(5), deadline.Token);
            Assert.Equal(recoverAccepted ? 1 : 0, status.Count);
            Assert.DoesNotContain("admin-", JsonSerializer.Serialize(status));
            var automaticRecovery = recoverAccepted && createCluster;
            if (recoverAccepted)
            {
                var blockedId = Guid.NewGuid();
                await client.CallAsync<BackgroundOperation>("cluster-defaults-save-start",
                    new ClusterDefaultsEdit(new(cluster.Id, createCluster ? "Created target" : "Target", ArkEdition.ASE, createCluster ? 30 : 80), "Changed", 90, true),
                    TimeSpan.FromSeconds(8), deadline.Token, blockedId);
                Assert.Equal(BackgroundOperationState.Failed, (await WaitForJob(blockedId)).State);
                var discardId = Guid.NewGuid();
                await client.CallAsync<BackgroundOperation>("presets-application-discard-start",
                    new PresetApplicationDiscard(servers[0].Id, new string('0', 64), true), TimeSpan.FromSeconds(8), deadline.Token, discardId);
                Assert.Equal(BackgroundOperationState.Failed, (await WaitForJob(discardId)).State);
                Assert.Single(await new ClusterJoiningJournal(root).ReadAsync());
            }
            if (automaticRecovery)
                await client.CallAsync<ServerStartupPreferences>("server-startup-save", new SaveServerStartupPreferences(new(1, [servers[0].Id], []), true),
                    TimeSpan.FromSeconds(5), deadline.Token);
            await client.CallAsync<BackgroundOperation>(automaticRecovery ? "server-startup-run" : recoverAccepted ? "cluster-joining-resume-start" : "cluster-membership-save-start",
                automaticRecovery ? null : recoverAccepted ? accepted!.OperationId : new ClusterMembershipEdit(expected, cluster.Id, true) { NewClusterName = createCluster ? "Created target" : null },
                TimeSpan.FromSeconds(8), deadline.Token, id);
            BackgroundOperation job;
            do
            {
                await Task.Delay(50, deadline.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(id), TimeSpan.FromSeconds(5), deadline.Token);
            } while (job.State is BackgroundOperationState.Queued or BackgroundOperationState.Running);
            Assert.True(job.State == BackgroundOperationState.Succeeded, job.Error);
            if (automaticRecovery) Assert.Empty(job.Result!.Value.Deserialize<LifecycleOperationResult>()!.Members);
            Assert.DoesNotContain("admin-", JsonSerializer.Serialize(job)); Assert.DoesNotContain("join-", JsonSerializer.Serialize(job));
            var result = await repository.ReadAsync(); Assert.Empty(await new ClusterJoiningJournal(root).ReadAsync());
            Assert.Empty(await client.CallAsync<List<ClusterJoiningStatus>>("cluster-joining-read", null, TimeSpan.FromSeconds(5), deadline.Token));
            foreach (var original in servers)
            {
                var actual = result.Servers.Single(s => s.Id == original.Id);
                Assert.Equal(cluster.Id, actual.ClusterId); Assert.Equal(createCluster ? 30 : 80, actual.Settings.MaxPlayers);
                Assert.Equal(original.Settings.Name, actual.Settings.Name); Assert.Equal(original.Settings.RconPort, actual.Settings.RconPort);
                var text = await File.ReadAllTextAsync(Path.Combine(original.Directory, "ShooterGame", "Saved", "Config", "WindowsServer", "GameUserSettings.ini"));
                Assert.Contains("TamingSpeedMultiplier=" + (createCluster ? 2 : 8), text);
                Assert.Contains("ServerAdminPassword=admin-" + servers.IndexOf(original), text);
                Assert.Contains("ServerPassword=join-" + servers.IndexOf(original), text);
            }
            Assert.DoesNotContain("admin-", JsonSerializer.Serialize(result.Clusters));
            Assert.DoesNotContain("join-", JsonSerializer.Serialize(result.Clusters));
            async Task<BackgroundOperation> WaitForJob(Guid jobId)
            {
                while (true)
                {
                    var snapshot = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(jobId), TimeSpan.FromSeconds(5), deadline.Token);
                    if (snapshot.State is not (BackgroundOperationState.Queued or BackgroundOperationState.Running)) return snapshot;
                    await Task.Delay(50, deadline.Token);
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
