using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Core.Tests;

public sealed class WelcomeHostIntegrationTests
{
    [Fact]
    public async Task ActualHostSavesWelcomeOverLocalConnectionAndRejectsStaleSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-welcome-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var playerListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        playerListener.Start();
        var rconPort = ((System.Net.IPEndPoint)playerListener.LocalEndpoint).Port;
        try
        {
            var directory = Path.Combine(root, "servers", "fixture");
            var platform = OperatingSystem.IsWindows() ? "Windows" : "Linux";
            var binary = Path.Combine(directory, "ShooterGame", "Binaries", platform == "Windows" ? "Win64" : "Linux",
                platform == "Windows" ? "ShooterGameServer.exe" : "ShooterGameServer");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            await File.WriteAllTextAsync(binary, "not an executable; never launched");
            var config = Path.Combine(directory, "ShooterGame", "Saved", "Config", platform + "Server", "GameUserSettings.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            await File.WriteAllTextAsync(config, $"; preserved\n[ServerSettings]\nServerAdminPassword=fixture-private\nRCONEnabled=True\nRCONPort={rconPort}\n");
            var id = Guid.NewGuid();
            await new CatalogRepository(Path.Combine(root, "catalog.json")).ChangeAsync(c => c.AddServer(new(id,
                ArkEdition.ASE, "TheIsland", directory, new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = rconPort })));
            var hostDirectory = Path.Combine(AppContext.BaseDirectory, "host-fixture");
            var executable = Path.Combine(hostDirectory, OperatingSystem.IsWindows() ? "ArkServerManager.Host.exe" : "ArkServerManager.Host");
            await using var host = ManagedProcess.Start(new(executable, hostDirectory, ["--data-root", root], Path.Combine(root, "host.log")));
            var client = new LocalManagerClient(root);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                try { await client.CallAsync<JsonElement>("ping", null, TimeSpan.FromMilliseconds(300), deadline.Token); break; }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException && !deadline.IsCancellationRequested)
                { if (!host.IsAlive) throw new IOException("Fixture host exited."); await Task.Delay(50, deadline.Token); }
            }
            var source = await client.CallAsync<ServerConfigurationFile>("server-configuration-read", new ServerConfigurationReadRequest(id, "GameUserSettings.ini"), TimeSpan.FromSeconds(5), deadline.Token);
            var playerNetwork = Task.Run(async () =>
            {
                using var peer = await playerListener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = peer.GetStream();
                var auth = await RconProtocol.ReadAsync(stream, deadline.Token);
                Assert.Equal("fixture-private", System.Text.Encoding.UTF8.GetString(auth.Body));
                await stream.WriteAsync(RconProtocol.Encode(auth.Id, 2, ""), deadline.Token);
                var command = await RconProtocol.ReadAsync(stream, deadline.Token);
                Assert.Equal("ListPlayers", System.Text.Encoding.UTF8.GetString(command.Body));
                await stream.WriteAsync(RconProtocol.Encode(command.Id, 0, "0. River, eos-account-42\n"), deadline.Token);
            }, deadline.Token);
            var players = await client.CallAsync<ServerPlayerQueryResult>("server-players-read", new ServerPlayerQueryRequest(id),
                TimeSpan.FromSeconds(8), deadline.Token);
            await playerNetwork;
            Assert.Equal(id, players.ServerId);
            Assert.Equal("River", Assert.Single(players.Players).Name);
            Assert.Equal("eos-account-42", players.Players[0].AccountId);
            Assert.False(players.CompleteListConfirmed);
            var announcementNetwork = Task.Run(async () =>
            {
                using var peer = await playerListener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = peer.GetStream();
                var auth = await RconProtocol.ReadAsync(stream, deadline.Token);
                Assert.Equal("fixture-private", System.Text.Encoding.UTF8.GetString(auth.Body));
                await stream.WriteAsync(RconProtocol.Encode(auth.Id, 2, ""), deadline.Token);
                var command = await RconProtocol.ReadAsync(stream, deadline.Token);
                Assert.Equal("Broadcast Welcome, survivors!", System.Text.Encoding.UTF8.GetString(command.Body));
                await stream.WriteAsync(RconProtocol.Encode(command.Id, 0, "Server received, But no response!!"), deadline.Token);
            }, deadline.Token);
            var announcementId = Guid.NewGuid();
            var announcement = new ServerAnnouncementRequest(id, "Welcome, survivors!", true);
            var announcementJob = await client.CallAsync<BackgroundOperation>("server-announcement-start", announcement,
                TimeSpan.FromSeconds(8), deadline.Token, announcementId);
            var announcementResult = await Finish(announcementId);
            Assert.Equal(BackgroundOperationState.Succeeded, announcementResult.State);
            Assert.Equal(id, announcementResult.Result!.Value.Deserialize<ServerAnnouncementResult>()!.ServerId);
            await announcementNetwork;
            var duplicate = await client.CallAsync<BackgroundOperation>("server-announcement-start", announcement,
                TimeSpan.FromSeconds(8), deadline.Token, announcementId);
            Assert.Equal(announcementJob.CreatedAt, duplicate.CreatedAt);
            Assert.Equal(BackgroundOperationState.Succeeded, duplicate.State);
            Assert.False(playerListener.Pending());
            var consoleNetwork = Task.Run(async () =>
            {
                using var peer = await playerListener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = peer.GetStream();
                var auth = await RconProtocol.ReadAsync(stream, deadline.Token);
                await stream.WriteAsync(RconProtocol.Encode(auth.Id, 2, ""), deadline.Token);
                var command = await RconProtocol.ReadAsync(stream, deadline.Token);
                Assert.Equal("GetGameLog", System.Text.Encoding.UTF8.GetString(command.Body));
                await stream.WriteAsync(RconProtocol.Encode(command.Id, 0, "fixture server response\nsecond line"), deadline.Token);
            }, deadline.Token);
            var commandId = Guid.NewGuid();
            var consoleCommand = new ServerConsoleCommand(id, "GetGameLog");
            var commandJob = await client.CallAsync<BackgroundOperation>("server-console-command-start", consoleCommand,
                TimeSpan.FromSeconds(8), deadline.Token, commandId);
            var commandResult = await Finish(commandId);
            Assert.Equal(BackgroundOperationState.Succeeded, commandResult.State);
            var consoleResponse = commandResult.Result!.Value.Deserialize<ServerConsoleResponse>()!;
            Assert.Equal(id, consoleResponse.ServerId);
            Assert.Equal("fixture server response\nsecond line", consoleResponse.Response);
            await consoleNetwork;
            Assert.Equal(commandJob.CreatedAt, (await client.CallAsync<BackgroundOperation>("server-console-command-start", consoleCommand,
                TimeSpan.FromSeconds(8), deadline.Token, commandId)).CreatedAt);
            Assert.False(playerListener.Pending());
            var selection = new ServerWelcomeEdit(id, source.SourceSHA256!, "Welcome from host", 37, true);
            var jobId = Guid.NewGuid();
            var started = await client.CallAsync<BackgroundOperation>("server-welcome-save-start", selection, TimeSpan.FromSeconds(5), deadline.Token, jobId);
            var finished = await Finish(jobId);
            Assert.Equal(BackgroundOperationState.Succeeded, finished.State);
            Assert.True(finished.Result!.Value.Deserialize<ServerWelcomeEditResult>()!.Saved);
            var saved = await File.ReadAllTextAsync(config);
            Assert.Contains("Message=Welcome from host", saved); Assert.Contains("Duration=37", saved);
            Assert.Contains("ServerAdminPassword=fixture-private", saved); Assert.Contains("; preserved", saved);
            Assert.Equal(started.CreatedAt, (await client.CallAsync<BackgroundOperation>("server-welcome-save-start", selection,
                TimeSpan.FromSeconds(5), deadline.Token, jobId)).CreatedAt);
            var stale = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("server-welcome-save-start", selection with { Message = "stale overwrite" }, TimeSpan.FromSeconds(5), deadline.Token, stale);
            Assert.Equal(BackgroundOperationState.Failed, (await Finish(stale)).State);
            Assert.Equal(saved, await File.ReadAllTextAsync(config));

            var advancedSource = await client.CallAsync<ServerConfigurationFile>("server-configuration-read",
                new ServerConfigurationReadRequest(id, "GameUserSettings.ini"), TimeSpan.FromSeconds(5), deadline.Token);
            var advancedId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("server-configuration-save-start", new ConfirmedConfigurationEdit
                { Confirmed = true, Edit = new(id, "GameUserSettings.ini", advancedSource.SourceSHA256!, advancedSource.Text + "\n[FixtureMod]\nOption=chosen\n") },
                TimeSpan.FromSeconds(5), deadline.Token, advancedId);
            var advancedResult = await Finish(advancedId);
            Assert.Equal(BackgroundOperationState.Succeeded, advancedResult.State);
            Assert.True(advancedResult.Result!.Value.Deserialize<ServerConfigurationEditResult>()!.Saved);
            saved = await File.ReadAllTextAsync(config);
            Assert.Contains("Option=chosen", saved);
            Assert.Contains("ServerAdminPassword=fixture-private", saved);

            var fixtureName = "ArkServerManager.ProcessFixture" + (OperatingSystem.IsWindows() ? ".exe" : "");
            foreach (var fixtureFile in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixture")))
                File.Copy(fixtureFile, Path.Combine(Path.GetDirectoryName(binary)!,
                    Path.GetFileName(fixtureFile) == fixtureName ? Path.GetFileName(binary) : Path.GetFileName(fixtureFile)), true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var running = ManagedProcess.Start(new(binary, Path.GetDirectoryName(binary)!, ["--wait"], Path.Combine(root, "running-fixture.log")));
            await running.WaitForConsoleAsync(0, line => line.Text == "ready", TimeSpan.FromSeconds(5), deadline.Token);
            var status = await client.CallAsync<RegisteredProcessStatus>("server-process-status", null,
                TimeSpan.FromSeconds(5), deadline.Token);
            Assert.Equal(RegisteredProcessState.Detected, Assert.Single(status.Servers).State);
            Assert.Equal(1, status.Servers[0].ProcessCount);
            var loaded = await client.CallAsync<ServerConfigurationFile>("server-configuration-read", new ServerConfigurationReadRequest(id, "GameUserSettings.ini"), TimeSpan.FromSeconds(5), deadline.Token);
            var refusedId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("server-welcome-save-start",
                selection with { SourceSHA256 = loaded.SourceSHA256!, Message = "running overwrite" }, TimeSpan.FromSeconds(5), deadline.Token, refusedId);
            var refused = await Finish(refusedId);
            Assert.Equal(BackgroundOperationState.Failed, refused.State);
            Assert.Contains("Stop the selected map", refused.Error);
            Assert.True(running.IsAlive);
            Assert.Equal(saved, await File.ReadAllTextAsync(config));
            var externalStopId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("lifecycle-execute-member", new LifecycleRequest(id, LifecycleAction.Stop),
                TimeSpan.FromSeconds(8), deadline.Token, externalStopId);
            var externalStop = (await Finish(externalStopId)).Result!.Value.Deserialize<LifecycleOperationResult>()!;
            Assert.False(externalStop.Success);
            Assert.True(running.IsAlive);
            var externalShutdownId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("lifecycle-stop-all", null, TimeSpan.FromSeconds(8), deadline.Token, externalShutdownId);
            Assert.False((await Finish(externalShutdownId)).Result!.Value.Deserialize<LifecycleOperationResult>()!.Success);
            Assert.True(running.IsAlive);
            Assert.True(await running.RequestStopAsync(ct => running.SendStandardInputAsync("stop", ct), TimeSpan.FromSeconds(3), deadline.Token));
            playerListener.Stop();
            var basicReview = await client.CallAsync<BasicSettingsReview>("server-basic-settings-read", id,
                TimeSpan.FromSeconds(8), deadline.Token);
            var basicEdit = new BasicSettingsEdit(basicReview, basicReview.Settings with { MaxPlayers = 71 }, SaveChoice.SaveForNextStart, true);
            var basicId = Guid.NewGuid();
            var basicJob = await client.CallAsync<BackgroundOperation>("server-basic-settings-save-start", basicEdit,
                TimeSpan.FromSeconds(8), deadline.Token, basicId);
            var basicCompleted = await Finish(basicId);
            Assert.Equal(BackgroundOperationState.Succeeded, basicCompleted.State);
            var basicSaved = basicCompleted.Result!.Value.Deserialize<BasicSettingsEditResult>()!;
            Assert.True(basicSaved.Save.Saved);
            Assert.False(basicSaved.Save.Pending);
            Assert.False(basicSaved.Save.Restarted);
            Assert.Contains("MaxPlayers=71", await File.ReadAllTextAsync(config));
            Assert.Equal(basicJob.CreatedAt, (await client.CallAsync<BackgroundOperation>("server-basic-settings-save-start", basicEdit,
                TimeSpan.FromSeconds(8), deadline.Token, basicId)).CreatedAt);
            var staleBasicId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("server-basic-settings-save-start", basicEdit,
                TimeSpan.FromSeconds(8), deadline.Token, staleBasicId);
            Assert.Equal(BackgroundOperationState.Failed, (await Finish(staleBasicId)).State);
            var fileReview = await client.CallAsync<BasicSettingsReview>("server-basic-settings-read", id,
                TimeSpan.FromSeconds(8), deadline.Token);
            await File.AppendAllTextAsync(config, "\n; external edit after review\n");
            var externallyChanged = await File.ReadAllTextAsync(config);
            var staleFileId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("server-basic-settings-save-start",
                new BasicSettingsEdit(fileReview, fileReview.Settings with { MaxPlayers = 72 }, SaveChoice.SaveForNextStart, true),
                TimeSpan.FromSeconds(8), deadline.Token, staleFileId);
            var staleFileResult = await Finish(staleFileId);
            Assert.Equal(BackgroundOperationState.Failed, staleFileResult.State);
            Assert.Contains("configuration changed", staleFileResult.Error);
            Assert.Equal(externallyChanged, await File.ReadAllTextAsync(config));
            var pendingSettingsPath = Path.Combine(root, "app-settings", "pending-settings.json");
            await new AtomicJsonStore<PendingSettingsDocument>(pendingSettingsPath).SaveAsync(new(1,
                [new(id, new() { Name = "Fixture updated", MaxPlayers = 75, GamePort = 7777, QueryPort = 27015, RconPort = rconPort })]));
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(binary)!, "native-server-fixture.json"),
                JsonSerializer.Serialize(new { Port = rconPort, Password = "fixture-private", SaveResponse = "World Saved" }));
            var restoreOperation = Guid.NewGuid();
            var oldWorld = Path.Combine(directory, ".world-restore-previous-" + restoreOperation.ToString("N"));
            Directory.CreateDirectory(oldWorld);
            await File.WriteAllTextAsync(Path.Combine(oldWorld, "world.ark"), "previous world fixture");
            var restoreJournal = new AtomicJsonStore<WorldRestoreRecord>(Path.Combine(directory, "backups", "restore-transactions", restoreOperation.ToString("N") + ".json"));
            var interruptedRestore = new WorldRestoreRecord(restoreOperation, id, ArkEdition.ASE, 1,
                Path.Combine(directory, "ShooterGame", "Saved"), Path.Combine(directory, ".world-restore-stage-" + restoreOperation.ToString("N")),
                oldWorld, WorldRestorePhase.Replacing);
            await restoreJournal.SaveAsync(interruptedRestore);
            var blockedStartId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("lifecycle-execute-member", new LifecycleRequest(id, LifecycleAction.Start),
                TimeSpan.FromSeconds(8), deadline.Token, blockedStartId);
            var blockedStart = (await Finish(blockedStartId)).Result!.Value.Deserialize<LifecycleOperationResult>()!;
            Assert.False(blockedStart.Success);
            Assert.Contains("interrupted world replacement", Assert.Single(blockedStart.Members).Error);
            Assert.True(Directory.Exists(oldWorld));
            await restoreJournal.SaveAsync(interruptedRestore with { Phase = WorldRestorePhase.Replaced });
            foreach (var action in new[] { LifecycleAction.Start, LifecycleAction.Restart, LifecycleAction.Stop })
            {
                var actionId = Guid.NewGuid();
                await client.CallAsync<BackgroundOperation>(action == LifecycleAction.Stop ? "lifecycle-stop-all" : "lifecycle-execute-member",
                    action == LifecycleAction.Stop ? null : new LifecycleRequest(id, action),
                    TimeSpan.FromSeconds(8), deadline.Token, actionId);
                var completed = await Finish(actionId);
                Assert.Equal(BackgroundOperationState.Succeeded, completed.State);
                var result = completed.Result!.Value.Deserialize<LifecycleOperationResult>()!;
                Assert.True(result.Success, Assert.Single(result.Members).Error);
                Assert.Equal(action == LifecycleAction.Stop ? ServerState.Stopped : ServerState.Running,
                    Assert.Single(result.Members).State);
                var runtimeStatus = await client.CallAsync<List<MemberOperationResult>>("lifecycle-status", null,
                    TimeSpan.FromSeconds(8), deadline.Token);
                Assert.True(Assert.Single(runtimeStatus).Managed);
                if (action == LifecycleAction.Start)
                {
                    Assert.False(Directory.Exists(oldWorld));
                    Assert.Equal(WorldRestorePhase.Cleaned, (await restoreJournal.LoadAsync())!.Phase);
                    var applied = Assert.Single((await client.CallAsync<CatalogDocument>("catalog", null,
                        TimeSpan.FromSeconds(8), deadline.Token)).Servers);
                    Assert.Equal("Fixture updated", applied.Settings.Name);
                    Assert.Equal(75, applied.Settings.MaxPlayers);
                    var appliedText = await File.ReadAllTextAsync(config);
                    Assert.Contains("SessionName=Fixture updated", appliedText);
                    Assert.Contains("MaxPlayers=75", appliedText);
                    Assert.Contains("Option=chosen", appliedText);
                    Assert.Contains("ServerAdminPassword=fixture-private", appliedText);
                    Assert.Empty((await new AtomicJsonStore<PendingSettingsDocument>(pendingSettingsPath).LoadAsync())!.Changes);
                    var runningReview = await client.CallAsync<BasicSettingsReview>("server-basic-settings-read", id,
                        TimeSpan.FromSeconds(8), deadline.Token);
                    var queuedId = Guid.NewGuid();
                    await client.CallAsync<BackgroundOperation>("server-basic-settings-save-start",
                        new BasicSettingsEdit(runningReview, runningReview.Settings with { MaxPlayers = 76 }, SaveChoice.SaveForNextStart, true),
                        TimeSpan.FromSeconds(8), deadline.Token, queuedId);
                    var queuedResult = await Finish(queuedId);
                    Assert.Equal(BackgroundOperationState.Succeeded, queuedResult.State);
                    Assert.True(queuedResult.Result!.Value.Deserialize<BasicSettingsEditResult>()!.Save.Pending);
                    Assert.Equal(appliedText, await File.ReadAllTextAsync(config));
                    var queuedReview = await client.CallAsync<BasicSettingsReview>("server-basic-settings-read", id,
                        TimeSpan.FromSeconds(8), deadline.Token);
                    Assert.Equal(75, queuedReview.Settings.MaxPlayers);
                    Assert.Equal(76, queuedReview.QueuedSettings!.MaxPlayers);
                    Assert.False(queuedReview.QueuedConfigurationChanged);
                    Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(queuedReview));
                    await File.AppendAllTextAsync(config, "\n; changed after queuing\n");
                    var conflictedReview = await client.CallAsync<BasicSettingsReview>("server-basic-settings-read", id,
                        TimeSpan.FromSeconds(8), deadline.Token);
                    Assert.True(conflictedReview.QueuedConfigurationChanged);
                    Assert.Equal(76, conflictedReview.QueuedSettings!.MaxPlayers);
                    await File.WriteAllTextAsync(config, appliedText);
                    var fixtureSettings = Path.Combine(Path.GetDirectoryName(binary)!, "native-server-fixture.json");
                    await File.WriteAllTextAsync(fixtureSettings, JsonSerializer.Serialize(new { Port = rconPort, Password = "fixture-private", SaveResponse = "Server received, But no response!!" }));
                    var failedStopId = Guid.NewGuid();
                    await client.CallAsync<BackgroundOperation>("lifecycle-execute-member", new LifecycleRequest(id, LifecycleAction.Stop),
                        TimeSpan.FromSeconds(8), deadline.Token, failedStopId);
                    var failedStop = (await Finish(failedStopId)).Result!.Value.Deserialize<LifecycleOperationResult>()!;
                    Assert.False(failedStop.Success);
                    Assert.Equal(ServerState.Running, Assert.Single(failedStop.Members).State);
                    Assert.Contains("did not confirm", failedStop.Members[0].Error);
                    var detected = await client.CallAsync<RegisteredProcessStatus>("server-process-status", null,
                        TimeSpan.FromSeconds(8), deadline.Token);
                    Assert.Equal(RegisteredProcessState.Detected, Assert.Single(detected.Servers).State);
                    await File.WriteAllTextAsync(fixtureSettings, JsonSerializer.Serialize(new { Port = rconPort, Password = "fixture-private", SaveResponse = "World Saved" }));
                }
                if (action == LifecycleAction.Restart)
                {
                    Assert.Contains("MaxPlayers=76", await File.ReadAllTextAsync(config));
                    Assert.Empty((await new AtomicJsonStore<PendingSettingsDocument>(pendingSettingsPath).LoadAsync())!.Changes);
                    var restartReview = await client.CallAsync<BasicSettingsReview>("server-basic-settings-read", id,
                        TimeSpan.FromSeconds(8), deadline.Token);
                    var restartSaveId = Guid.NewGuid();
                    await client.CallAsync<BackgroundOperation>("server-basic-settings-save-start",
                        new BasicSettingsEdit(restartReview, restartReview.Settings with { MaxPlayers = 77 }, SaveChoice.SaveAndRestart, true),
                        TimeSpan.FromSeconds(8), deadline.Token, restartSaveId);
                    var restartSaved = await Finish(restartSaveId);
                    Assert.Equal(BackgroundOperationState.Succeeded, restartSaved.State);
                    var restartSave = restartSaved.Result!.Value.Deserialize<BasicSettingsEditResult>()!.Save;
                    Assert.True(restartSave.Saved);
                    Assert.True(restartSave.Restarted);
                    Assert.False(restartSave.Pending);
                    Assert.Contains("MaxPlayers=77", await File.ReadAllTextAsync(config));
                    var afterRestartSave = await client.CallAsync<List<MemberOperationResult>>("lifecycle-status", null,
                        TimeSpan.FromSeconds(8), deadline.Token);
                    Assert.Equal(ServerState.Running, Assert.Single(afterRestartSave).State);
                }
            }
            Directory.CreateDirectory(Path.Combine(directory, "backups"));
            var restoreSource = Path.Combine(root, "world-restore-source");
            Directory.CreateDirectory(Path.Combine(restoreSource, "SavedArks", "map"));
            Directory.CreateDirectory(Path.Combine(restoreSource, "SaveGames"));
            await File.WriteAllTextAsync(Path.Combine(restoreSource, "SavedArks", "map", "world.ark"), "restored world fixture");
            await File.WriteAllTextAsync(Path.Combine(restoreSource, "SaveGames", "player.arkprofile"), "restored player fixture");
            var liveSaved = Path.Combine(directory, "ShooterGame", "Saved");
            Directory.CreateDirectory(Path.Combine(liveSaved, "SavedArks", "map"));
            Directory.CreateDirectory(Path.Combine(liveSaved, "SaveGames"));
            await File.WriteAllTextAsync(Path.Combine(liveSaved, "SavedArks", "map", "world.ark"), "current world fixture");
            await File.WriteAllTextAsync(Path.Combine(liveSaved, "SaveGames", "new.arkprofile"), "new player fixture");
            var configurationBeforeRestore = await File.ReadAllTextAsync(config);
            var archiveName = "hour-00000001.zip";
            var worldManifest = await new SnapshotArchive().CreateAsync(restoreSource, Path.Combine(directory, "backups", archiveName),
                id, ArkEdition.ASE, 1, layout: SnapshotLayout.WorldSavedFolders);
            await new AtomicJsonStore<AutomaticBackupState>(Path.Combine(directory, "backups", "backup-history.json")).SaveAsync(
                new(1, id, ArkEdition.ASE, TimeSpan.FromHours(1).Ticks, null, null, [new(1, archiveName, worldManifest.CreatedAt)], null));
            var restoreId = Guid.NewGuid();
            var restoreSelection = new WorldRestoreRequest(id, 1, true, true);
            var restoreJob = await client.CallAsync<BackgroundOperation>("server-world-restore-start", restoreSelection,
                TimeSpan.FromSeconds(8), deadline.Token, restoreId);
            var restoreCompleted = await Finish(restoreId);
            Assert.Equal(BackgroundOperationState.Succeeded, restoreCompleted.State);
            var restoreOutcome = restoreCompleted.Result!.Value.Deserialize<WorldRestoreOperationResult>()!;
            Assert.True(restoreOutcome.Restore.Restored);
            Assert.True(restoreOutcome.Restore.Restarted);
            Assert.Null(restoreOutcome.Restore.PreviousStateDirectory);
            Assert.Equal("restored world fixture", await File.ReadAllTextAsync(Path.Combine(liveSaved, "SavedArks", "map", "world.ark")));
            Assert.Equal("restored player fixture", await File.ReadAllTextAsync(Path.Combine(liveSaved, "SaveGames", "player.arkprofile")));
            Assert.False(File.Exists(Path.Combine(liveSaved, "SaveGames", "new.arkprofile")));
            Assert.Equal(configurationBeforeRestore, await File.ReadAllTextAsync(config));
            var restoreRecords = await client.CallAsync<List<WorldRestoreRecord>>("server-world-restore-records", id,
                TimeSpan.FromSeconds(8), deadline.Token);
            Assert.Equal(WorldRestorePhase.Cleaned, restoreRecords.Single(r => r.OperationId == restoreId).Phase);
            Assert.Equal(restoreJob.CreatedAt, (await client.CallAsync<BackgroundOperation>("server-world-restore-start", restoreSelection,
                TimeSpan.FromSeconds(8), deadline.Token, restoreId)).CreatedAt);
            await File.WriteAllTextAsync(Path.Combine(directory, "backups", "fixture-backup"), "associated backup");
            Assert.True(File.Exists(Path.Combine(directory, "logs", "console.log")));
            var deleteId = Guid.NewGuid();
            var deletion = new ServerDeletionSelection(id, "Fixture updated");
            var deletionJob = await client.CallAsync<BackgroundOperation>("server-deletion-start", deletion,
                TimeSpan.FromSeconds(8), deadline.Token, deleteId);
            var deletionResult = await Finish(deleteId);
            Assert.Equal(BackgroundOperationState.Succeeded, deletionResult.State);
            var deletionRecord = deletionResult.Result!.Value.Deserialize<ServerDeletionRecord>()!;
            Assert.Equal(ServerDeletionPhase.FilesRemoved, deletionRecord.Phase);
            Assert.Equal(deleteId, deletionRecord.OperationId);
            Assert.False(Directory.Exists(directory));
            var durableDeletion = await client.CallAsync<RecordedDeletionStatus>("server-deletion-record", deleteId,
                TimeSpan.FromSeconds(8), deadline.Token);
            Assert.Equal(ServerDeletionPhase.FilesRemoved, durableDeletion.Record.Phase);
            Assert.Equal(DeletionRecoveryAction.None, durableDeletion.Assessment.Action);
            Assert.Empty((await client.CallAsync<CatalogDocument>("catalog", null, TimeSpan.FromSeconds(8), deadline.Token)).Servers);
            Assert.Equal(deletionJob.CreatedAt, (await client.CallAsync<BackgroundOperation>("server-deletion-start", deletion,
                TimeSpan.FromSeconds(8), deadline.Token, deleteId)).CreatedAt);

            async Task<BackgroundOperation> Finish(Guid job)
            {
                while (true)
                {
                    var result = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(job), TimeSpan.FromSeconds(5), deadline.Token);
                    if (result.FinishedAt is not null) return result;
                    await Task.Delay(25, deadline.Token);
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
