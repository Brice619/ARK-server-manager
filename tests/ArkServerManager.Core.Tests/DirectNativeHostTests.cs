using ArkServerManager.Core;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace ArkServerManager.Core.Tests;

public sealed class DirectNativeHostTests
{
    [Fact]
    public async Task ActualHostStartsAndRestartsWithoutGameFileSynchronizationOrDriveMappings()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ark-direct host with spaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var records = Path.Combine(root, "app-settings", "native-launch-drives.json");
        var client = new LocalManagerClient(root);
        ManagedProcess? host = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var cluster = Guid.NewGuid(); var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var definitions = new List<ServerDefinition>();
            for (var index = 0; index < 2; index++)
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var directory = Path.Combine(root, "servers", "map-" + index);
                var binaryDirectory = Path.Combine(directory, "ShooterGame", "Binaries", "Win64");
                Directory.CreateDirectory(binaryDirectory);
                foreach (var fixture in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixture")))
                    File.Copy(fixture, Path.Combine(binaryDirectory, Path.GetFileName(fixture) == "ArkServerManager.ProcessFixture.exe"
                        ? "ArkAscendedServer.exe" : Path.GetFileName(fixture)));
                await File.WriteAllTextAsync(Path.Combine(binaryDirectory, "native-server-fixture.json"),
                    JsonSerializer.Serialize(new { Port = port, Password = "fixture-private", SaveResponse = "World Saved", ReadAdministratorFromConfiguration = true }));
                var config = Path.Combine(directory, "ShooterGame", "Saved", "Config", "WindowsServer");
                Directory.CreateDirectory(config);
                await File.WriteAllTextAsync(Path.Combine(config, "GameUserSettings.ini"),
                    $"[ServerSettings]\nServerAdminPassword=fixture-private\nRCONEnabled=True\nRCONPort={port}\n");
                definitions.Add(new(ids[index], ArkEdition.ASA, index == 0 ? "TheIsland_WP" : "ScorchedEarth_WP", directory,
                    new() { Name = "Map " + index, GamePort = 7777 + 10 * index, QueryPort = 27015 + index, RconPort = port }, cluster) { Mods = ["928708"] });
            }
            await repository.ChangeAsync(c =>
            {
                c.AddCluster(new(cluster, "Fixture cluster", ArkEdition.ASA, definitions[0].Settings));
                foreach (var definition in definitions) c.AddServer(definition);
            });
            // Startup must not use game-package validation as a prerequisite. This
            // intentionally unusable package manifest would fail any full synchronization.
            var pool=Path.Combine(root,"GameFiles","ASA","Windows");Directory.CreateDirectory(pool);
            var packageManifest=Path.Combine(pool,"package.json");
            const string untouchedPackage="startup-must-not-read-or-repair-this-manifest";
            await File.WriteAllTextAsync(packageManifest,untouchedPackage);
            var markerSnapshots=new Dictionary<string,string>();
            foreach(var definition in definitions)
            {
                var marker=Path.Combine(definition.Directory,SharedServerFiles.Marker);
                var text=JsonSerializer.Serialize(new SharedServerFileRecord(1,root,ArkEdition.ASA,ServerPlatform.Windows,[]));
                await File.WriteAllTextAsync(marker,text);markerSnapshots[marker]=text;
            }
            var hostDirectory = Path.Combine(AppContext.BaseDirectory, "host-fixture");
            host = ManagedProcess.Start(new(Path.Combine(hostDirectory, "ArkServerManager.Host.exe"), hostDirectory,
                ["--data-root", root], Path.Combine(root, "host.log")));
            while (true)
            {
                try { await client.CallAsync<JsonElement>("ping", null, TimeSpan.FromMilliseconds(300), deadline.Token); break; }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException && !deadline.IsCancellationRequested)
                { if (!host.IsAlive) throw new IOException("Fixture host exited."); await Task.Delay(50, deadline.Token); }
            }
            var defaultsReview = await client.CallAsync<ClusterDefaultsReview>("cluster-defaults-read", cluster, TimeSpan.FromSeconds(5), deadline.Token);
            var defaultsEdit = new ClusterDefaultsEdit(defaultsReview, "Renamed fixture cluster", 90, true);
            var defaultsJob = await AccessAction("cluster-defaults-save-start", defaultsEdit);
            var defaultsSaved = defaultsJob.Result!.Value.Deserialize<ClusterDefaultsReview>()!;
            Assert.Equal("Renamed fixture cluster", defaultsSaved.Name); Assert.Equal(90, defaultsSaved.MaxPlayers);
            Assert.Equal(defaultsJob.CreatedAt, (await client.CallAsync<BackgroundOperation>("cluster-defaults-save-start", defaultsEdit,
                TimeSpan.FromSeconds(5), deadline.Token, defaultsJob.Id)).CreatedAt);
            await Assert.ThrowsAsync<IOException>(() => AccessAction("cluster-defaults-save-start", defaultsEdit));
            Assert.All((await repository.ReadAsync(deadline.Token)).Servers, server => Assert.Equal(70, server.Settings.MaxPlayers));
            Assert.Empty((await client.CallAsync<ServerStartupPreferences>("server-startup-read", null, TimeSpan.FromSeconds(5), deadline.Token)).Clusters);
            Assert.Empty((await ActionAsync("server-startup-run", null)).Members);
            await client.CallAsync<ServerStartupPreferences>("server-startup-save",
                new SaveServerStartupPreferences(new(1, [], [cluster]), true), TimeSpan.FromSeconds(5), deadline.Token);
            var automaticStart = await ActionAsync("server-startup-run", null);
            Assert.True(automaticStart.Success);
            Assert.Equal(ids.Order(), automaticStart.Members.Select(m => m.ServerId).Order());
            Assert.False(File.Exists(records));
            Assert.Equal(untouchedPackage,await File.ReadAllTextAsync(packageManifest));
            foreach(var marker in markerSnapshots)Assert.Equal(marker.Value,await File.ReadAllTextAsync(marker.Key));
            var directProcesses=await new ServerProcessDiscovery().ScanAsync(deadline.Token);
            foreach(var definition in definitions)
            {
                var observed=Assert.Single(directProcesses.Candidates,c=>c.Directory.Equals(definition.Directory,StringComparison.OrdinalIgnoreCase));
                using var process=System.Diagnostics.Process.GetProcessById(observed.ProcessId);
                Assert.StartsWith(definition.Directory,process.MainModule!.FileName,StringComparison.OrdinalIgnoreCase);
            }
            foreach (var definition in definitions)
                Assert.Equal(InstallationStopState.Running, (await new InstallationStopGuard().CheckAsync(definition.Directory)).State);
            // Exercise the real manager/lifecycle connection with two owned test processes.
            // These fixtures authenticate RCON; they do not simulate ARK player joins.
            var selectedConfig = Path.Combine(definitions[0].Directory, "ShooterGame", "Saved", "Config", "WindowsServer", "GameUserSettings.ini");
            var unchangedConfig = await File.ReadAllTextAsync(selectedConfig);
            var accessReview = await client.CallAsync<ServerAccessReview>("server-access-read", ids[0], TimeSpan.FromSeconds(5), deadline.Token);
            var queuedJob = await AccessAction("server-access-save-start",
                new ServerAccessSave(new(accessReview, null, "queued-join-secret"), SaveChoice.SaveForNextStart, true));
            var queuedSave = queuedJob.Result!.Value.Deserialize<ServerAccessSaveResult>()!.Save;
            Assert.True(queuedSave.Pending); Assert.False(queuedSave.Restarted);
            Assert.Equal(unchangedConfig, await File.ReadAllTextAsync(selectedConfig));
            var queuedReview = await client.CallAsync<ServerAccessReview>("server-access-read", ids[0], TimeSpan.FromSeconds(5), deadline.Token);
            Assert.NotNull(queuedReview.PendingId); Assert.False(queuedReview.Applying);
            var canceled = await AccessAction("server-access-discard-start", new ServerAccessDiscard(ids[0], queuedReview.PendingId!.Value, true));
            Assert.True(canceled.Result!.Value.Deserialize<ServerAccessDiscardResult>()!.Discarded);
            Assert.Equal(unchangedConfig, await File.ReadAllTextAsync(selectedConfig));
            var freshReview = await client.CallAsync<ServerAccessReview>("server-access-read", ids[0], TimeSpan.FromSeconds(5), deadline.Token);
            Assert.Null(freshReview.PendingId);
            var beforeAccessRestart = await new ServerProcessDiscovery().ScanAsync(deadline.Token);
            var originalSelected = beforeAccessRestart.Candidates.Single(c => c.Directory.Equals(definitions[0].Directory, StringComparison.OrdinalIgnoreCase));
            var originalSibling = beforeAccessRestart.Candidates.Single(c => c.Directory.Equals(definitions[1].Directory, StringComparison.OrdinalIgnoreCase));
            var restartedAccess = await AccessAction("server-access-save-start",
                new ServerAccessSave(new(freshReview, "replacement-admin-secret", "restart-join-secret"), SaveChoice.SaveAndRestart, true));
            var restartedSave = restartedAccess.Result!.Value.Deserialize<ServerAccessSaveResult>()!.Save;
            Assert.True(restartedSave.Restarted); Assert.False(restartedSave.Pending);
            var afterAccessRestart = await new ServerProcessDiscovery().ScanAsync(deadline.Token);
            var restartedSelected = afterAccessRestart.Candidates.Single(c => c.Directory.Equals(definitions[0].Directory, StringComparison.OrdinalIgnoreCase));
            var unchangedSibling = afterAccessRestart.Candidates.Single(c => c.Directory.Equals(definitions[1].Directory, StringComparison.OrdinalIgnoreCase));
            Assert.True(restartedSelected.ProcessId != originalSelected.ProcessId || restartedSelected.StartedAt != originalSelected.StartedAt);
            Assert.Equal(originalSibling.ProcessId, unchangedSibling.ProcessId); Assert.Equal(originalSibling.StartedAt, unchangedSibling.StartedAt);
            Assert.Contains("ServerPassword=restart-join-secret", await File.ReadAllTextAsync(selectedConfig));
            Assert.Contains("ServerAdminPassword=replacement-admin-secret", await File.ReadAllTextAsync(selectedConfig));
            Assert.DoesNotContain("restart-join-secret", JsonSerializer.Serialize(restartedAccess));
            Assert.DoesNotContain("replacement-admin-secret", JsonSerializer.Serialize(restartedAccess));
            Assert.Null((await client.CallAsync<ServerAccessReview>("server-access-read", ids[0], TimeSpan.FromSeconds(5), deadline.Token)).PendingId);
            var localNow = DateTime.Now;
            var scheduledTime = localNow.Second > 40 ? localNow.AddMinutes(1) : localNow;
            await AccessAction("server-restart-schedule-save-start", new DailyRestartSave(ids[0],
                new TimeOnly(scheduledTime.Hour, scheduledTime.Minute), true, true));
            while (true)
            {
                var scan = await new ServerProcessDiscovery().ScanAsync(deadline.Token);
                var selected = scan.Candidates.SingleOrDefault(c => c.Directory.Equals(definitions[0].Directory, StringComparison.OrdinalIgnoreCase));
                var status = await client.CallAsync<List<MemberOperationResult>>("lifecycle-status", null, TimeSpan.FromSeconds(8), deadline.Token);
                if (selected is not null && (selected.ProcessId != restartedSelected.ProcessId || selected.StartedAt != restartedSelected.StartedAt)
                    && status.Single(s => s.ServerId == ids[0]).State == ServerState.Running)
                {
                    var sibling = scan.Candidates.Single(c => c.Directory.Equals(definitions[1].Directory, StringComparison.OrdinalIgnoreCase));
                    Assert.Equal(originalSibling.ProcessId, sibling.ProcessId); Assert.Equal(originalSibling.StartedAt, sibling.StartedAt);
                    var schedule = await client.CallAsync<DailyRestartSchedule>("server-restart-schedule-read", ids[0], TimeSpan.FromSeconds(5), deadline.Token);
                    Assert.Equal(DateOnly.FromDateTime(scheduledTime), schedule.LastAttemptDate);
                    break;
                }
                await Task.Delay(200, deadline.Token);
            }
            await AccessAction("server-restart-schedule-save-start", new DailyRestartSave(ids[0], new(4, 0), false, true));
            var sharedDraft = new ServerPreset("Shared running settings", ArkEdition.ASA,
                definitions[0].Settings with { MaxPlayers = 45 }, ["928708"])
            { UserSettings = "[ServerSettings]\nTamingSpeedMultiplier=6\n", GameSettings = "" };
            var clusterReview = await client.CallAsync<ClusterPresetReview>("cluster-preset-review",
                new ClusterPresetSelection(cluster, Guid.NewGuid(), [ids[0]]) { DraftPreset = sharedDraft }, TimeSpan.FromSeconds(8), deadline.Token);
            var beforeShared = await File.ReadAllTextAsync(selectedConfig);
            var sharedQueued = await AccessAction("cluster-preset-save-start",
                new ClusterPresetSave(clusterReview, SaveChoice.SaveForNextStart, true) { UseForNewMaps = true });
            Assert.DoesNotContain("replacement-admin-secret", JsonSerializer.Serialize(sharedQueued));
            Assert.Equal(beforeShared, await File.ReadAllTextAsync(selectedConfig));
            Assert.Equal(70, (await repository.ReadAsync()).Servers.Single(s => s.Id == ids[0]).Settings.MaxPlayers);
            var beforeSharedRestart = await new ServerProcessDiscovery().ScanAsync(deadline.Token);
            var selectedBeforeShared = beforeSharedRestart.Candidates.Single(c => c.Directory.Equals(definitions[0].Directory, StringComparison.OrdinalIgnoreCase));
            Assert.True((await ActionAsync("lifecycle-execute-member", new LifecycleRequest(ids[0], LifecycleAction.Restart))).Success);
            Assert.Contains("TamingSpeedMultiplier=6", await File.ReadAllTextAsync(selectedConfig));
            Assert.Contains("ServerAdminPassword=replacement-admin-secret", await File.ReadAllTextAsync(selectedConfig));
            Assert.Equal(45, (await repository.ReadAsync()).Servers.Single(s => s.Id == ids[0]).Settings.MaxPlayers);
            var afterSharedRestart = await new ServerProcessDiscovery().ScanAsync(deadline.Token);
            var selectedAfterShared = afterSharedRestart.Candidates.Single(c => c.Directory.Equals(definitions[0].Directory, StringComparison.OrdinalIgnoreCase));
            var siblingAfterShared = afterSharedRestart.Candidates.Single(c => c.Directory.Equals(definitions[1].Directory, StringComparison.OrdinalIgnoreCase));
            Assert.True(selectedBeforeShared.ProcessId != selectedAfterShared.ProcessId || selectedBeforeShared.StartedAt != selectedAfterShared.StartedAt);
            Assert.Equal(originalSibling.ProcessId, siblingAfterShared.ProcessId); Assert.Equal(originalSibling.StartedAt, siblingAfterShared.StartedAt);
            Assert.Equal(70, (await repository.ReadAsync()).Servers.Single(s => s.Id == ids[1]).Settings.MaxPlayers);
            var keepCapacity = await client.CallAsync<ClusterOverrideReview>("cluster-overrides-read", ids[0], TimeSpan.FromSeconds(8), deadline.Token);
            await AccessAction("cluster-overrides-save-start", new ClusterOverrideSave(keepCapacity, [], true, false, true));
            var sharedCapacity = await client.CallAsync<ClusterDefaultsReview>("cluster-defaults-read", cluster, TimeSpan.FromSeconds(8), deadline.Token);
            await AccessAction("cluster-defaults-save-start", new ClusterDefaultsEdit(sharedCapacity, sharedCapacity.Name, 65, true));
            var inheritCapacity = await client.CallAsync<ClusterOverrideReview>("cluster-overrides-read", ids[0], TimeSpan.FromSeconds(8), deadline.Token);
            var queuedInheritance = await AccessAction("cluster-overrides-save-start", new ClusterOverrideSave(inheritCapacity, [], false, false, true)
                { Choice = SaveChoice.SaveForNextStart });
            Assert.True(queuedInheritance.Result!.Value.Deserialize<ClusterOverrideSaveResult>()!.Applied!.Save.Pending);
            var beforeInheritance = (await repository.ReadAsync()).Servers.Single(s => s.Id == ids[0]);
            Assert.True(beforeInheritance.OverrideClusterMaxPlayers); Assert.Equal(45, beforeInheritance.Settings.MaxPlayers);
            var conflictingOverride = await client.CallAsync<ClusterOverrideReview>("cluster-overrides-read", ids[0], TimeSpan.FromSeconds(8), deadline.Token);
            await Assert.ThrowsAsync<IOException>(() => AccessAction("cluster-overrides-save-start", new ClusterOverrideSave(conflictingOverride, [], false, false, true)));
            Assert.True((await ActionAsync("lifecycle-execute-member", new LifecycleRequest(ids[0], LifecycleAction.Restart))).Success);
            var afterInheritance = (await repository.ReadAsync()).Servers.Single(s => s.Id == ids[0]);
            Assert.False(afterInheritance.OverrideClusterMaxPlayers); Assert.Equal(65, afterInheritance.Settings.MaxPlayers);
            var inheritanceScan = await new ServerProcessDiscovery().ScanAsync(deadline.Token);
            var inheritanceSibling = inheritanceScan.Candidates.Single(c => c.Directory.Equals(definitions[1].Directory, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(originalSibling.ProcessId, inheritanceSibling.ProcessId); Assert.Equal(originalSibling.StartedAt, inheritanceSibling.StartedAt);
            var advancedSource = await client.CallAsync<ServerConfigurationFile>("server-configuration-read",
                new ServerConfigurationReadRequest(ids[0], "GameUserSettings.ini"), TimeSpan.FromSeconds(8), deadline.Token);
            var advancedText = advancedSource.Text + "\n[AdvancedFixture]\nOption=queued\n";
            var advancedQueued = await AccessAction("server-configuration-save-start", new ConfirmedConfigurationEdit
            {
                Confirmed = true, Choice = SaveChoice.SaveForNextStart,
                Edit = new(ids[0], "GameUserSettings.ini", advancedSource.SourceSHA256!, advancedText)
            });
            Assert.True(advancedQueued.Result!.Value.Deserialize<ServerConfigurationEditResult>()!.Save!.Pending);
            Assert.Equal(advancedSource.Text, await File.ReadAllTextAsync(selectedConfig));
            Assert.DoesNotContain("replacement-admin-secret", JsonSerializer.Serialize(advancedQueued));
            var pendingAdvanced = await client.CallAsync<List<PendingConfigurationStatus>>("server-configuration-pending", ids[0], TimeSpan.FromSeconds(8), deadline.Token);
            Assert.Equal(advancedQueued.Id, Assert.Single(pendingAdvanced).Id);
            var advancedReview = await client.CallAsync<PendingConfigurationReview>("server-configuration-pending-review",
                new PendingConfigurationReviewRequest(ids[0], advancedQueued.Id), TimeSpan.FromSeconds(8), deadline.Token);
            Assert.Equal(advancedText, advancedReview.Text); Assert.False(advancedReview.Started);
            Assert.DoesNotContain("replacement-admin-secret", advancedReview.ToString());
            Assert.Equal(advancedSource.Text, await File.ReadAllTextAsync(selectedConfig));
            var conflictingAccess = await client.CallAsync<ServerAccessReview>("server-access-read", ids[0], TimeSpan.FromSeconds(8), deadline.Token);
            await Assert.ThrowsAsync<IOException>(() => AccessAction("server-access-save-start",
                new ServerAccessSave(new(conflictingAccess, null, "must-not-queue"), SaveChoice.SaveForNextStart, true)));
            var advancedDiscard = await AccessAction("server-configuration-discard-start", new PendingConfigurationDiscard(ids[0], advancedQueued.Id, true));
            Assert.True(advancedDiscard.Result!.Value.Deserialize<PendingConfigurationDiscardResult>()!.Discarded);
            Assert.Equal(advancedSource.Text, await File.ReadAllTextAsync(selectedConfig));
            Assert.Empty(await client.CallAsync<List<PendingConfigurationStatus>>("server-configuration-pending", ids[0], TimeSpan.FromSeconds(8), deadline.Token));
            await AccessAction("server-configuration-save-start", new ConfirmedConfigurationEdit
            {
                Confirmed = true, Choice = SaveChoice.SaveForNextStart,
                Edit = new(ids[0], "GameUserSettings.ini", advancedSource.SourceSHA256!, advancedText)
            });
            Assert.True((await ActionAsync("lifecycle-execute-member", new LifecycleRequest(ids[0], LifecycleAction.Restart))).Success);
            Assert.Contains("Option=queued", await File.ReadAllTextAsync(selectedConfig));
            Assert.Empty(await client.CallAsync<List<PendingConfigurationStatus>>("server-configuration-pending", ids[0], TimeSpan.FromSeconds(8), deadline.Token));
            var advancedFresh = await client.CallAsync<ServerConfigurationFile>("server-configuration-read",
                new ServerConfigurationReadRequest(ids[0], "GameUserSettings.ini"), TimeSpan.FromSeconds(8), deadline.Token);
            var beforeAdvancedRestart = await new ServerProcessDiscovery().ScanAsync(deadline.Token);
            var advancedSelected = beforeAdvancedRestart.Candidates.Single(c => c.Directory.Equals(definitions[0].Directory, StringComparison.OrdinalIgnoreCase));
            var advancedRestarted = await AccessAction("server-configuration-save-start", new ConfirmedConfigurationEdit
            {
                Confirmed = true, Choice = SaveChoice.SaveAndRestart,
                Edit = new(ids[0], "GameUserSettings.ini", advancedFresh.SourceSHA256!, advancedFresh.Text!.Replace("Option=queued", "Option=restarted"))
            });
            Assert.True(advancedRestarted.Result!.Value.Deserialize<ServerConfigurationEditResult>()!.Save!.Restarted);
            Assert.Contains("Option=restarted", await File.ReadAllTextAsync(selectedConfig));
            var afterAdvancedRestart = await new ServerProcessDiscovery().ScanAsync(deadline.Token);
            var advancedReplacement = afterAdvancedRestart.Candidates.Single(c => c.Directory.Equals(definitions[0].Directory, StringComparison.OrdinalIgnoreCase));
            var advancedSibling = afterAdvancedRestart.Candidates.Single(c => c.Directory.Equals(definitions[1].Directory, StringComparison.OrdinalIgnoreCase));
            Assert.True(advancedSelected.ProcessId != advancedReplacement.ProcessId || advancedSelected.StartedAt != advancedReplacement.StartedAt);
            Assert.Equal(originalSibling.ProcessId, advancedSibling.ProcessId); Assert.Equal(originalSibling.StartedAt, advancedSibling.StartedAt);
            var beforeCrash = await new ServerProcessDiscovery().ScanAsync(deadline.Token);
            var selectedProcess = beforeCrash.Candidates.Single(c => c.Directory.Equals(definitions[0].Directory, StringComparison.OrdinalIgnoreCase));
            var siblingProcess = beforeCrash.Candidates.Single(c => c.Directory.Equals(definitions[1].Directory, StringComparison.OrdinalIgnoreCase));
            var endpoint = await ServerRconConfiguration.ReadAsync(repository, ids[0], TimeSpan.FromSeconds(3), RconResponseMode.SinglePacket, deadline.Token);
            await Assert.ThrowsAnyAsync<IOException>(() => new RconClient().ExecuteAsync(endpoint, "FixtureCrash", deadline.Token));
            while (true)
            {
                var scan = await new ServerProcessDiscovery().ScanAsync(deadline.Token);
                var recovered = scan.Candidates.SingleOrDefault(c => c.Directory.Equals(definitions[0].Directory, StringComparison.OrdinalIgnoreCase));
                var status = await client.CallAsync<List<MemberOperationResult>>("lifecycle-status", null, TimeSpan.FromSeconds(8), deadline.Token);
                if (recovered is not null && (recovered.ProcessId != selectedProcess.ProcessId || recovered.StartedAt != selectedProcess.StartedAt)
                    && status.Single(s => s.ServerId == ids[0]).State == ServerState.Running)
                {
                    var sibling = scan.Candidates.Single(c => c.Directory.Equals(definitions[1].Directory, StringComparison.OrdinalIgnoreCase));
                    Assert.Equal(siblingProcess.ProcessId, sibling.ProcessId); Assert.Equal(siblingProcess.StartedAt, sibling.StartedAt);
                    break;
                }
                await Task.Delay(50, deadline.Token);
            }
            var incidents = await new ActivityJournal(Path.Combine(root, "logs", "activity.jsonl")).ReadRecentAsync(100, ids[0], deadline.Token);
            Assert.Contains(incidents.Entries, entry => entry.Category == "server" && entry.ExitCode == 7);
            Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(incidents));
            Assert.DoesNotContain("replacement-admin-secret", JsonSerializer.Serialize(incidents));
            Assert.True((await ActionAsync("lifecycle-execute-member", new LifecycleRequest(ids[0], LifecycleAction.Stop))).Success);
            var statuses = await client.CallAsync<List<MemberOperationResult>>("lifecycle-status", null, TimeSpan.FromSeconds(5), deadline.Token);
            Assert.Equal(ServerState.Running, statuses.Single(s => s.ServerId == ids[1]).State);
            Assert.True((await ActionAsync("lifecycle-stop-all", null)).Success);

            Assert.Equal(untouchedPackage,await File.ReadAllTextAsync(packageManifest));
            foreach(var marker in markerSnapshots)Assert.Equal(marker.Value,await File.ReadAllTextAsync(marker.Key));

            async Task<BackgroundOperation> AccessAction(string operation, object? payload)
            {
                var requestId = Guid.NewGuid();
                await client.CallAsync<BackgroundOperation>(operation, payload, TimeSpan.FromSeconds(8), deadline.Token, requestId);
                while (true)
                {
                    var snapshot = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(requestId), TimeSpan.FromSeconds(8), deadline.Token);
                    if (snapshot.State == BackgroundOperationState.Succeeded) return snapshot;
                    if (snapshot.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled) throw new IOException(snapshot.Error);
                    await Task.Delay(50, deadline.Token);
                }
            }
            async Task<LifecycleOperationResult> ActionAsync(string operation, object? payload) =>
                (await AccessAction(operation, payload)).Result!.Value.Deserialize<LifecycleOperationResult>()!;
        }
        finally
        {
            // Stop fixtures before disposing the Host; its children must not survive a failed assertion.
            if (host is { IsAlive: true })
            {
                try
                {
                    var job = await client.CallAsync<BackgroundOperation>("lifecycle-stop-all", null, TimeSpan.FromSeconds(8));
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while (job.State is BackgroundOperationState.Queued or BackgroundOperationState.Running)
                    { await Task.Delay(50, cleanup.Token); job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(job.Id), TimeSpan.FromSeconds(5), cleanup.Token); }
                }
                finally { await host.DisposeAsync(); }
            }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
