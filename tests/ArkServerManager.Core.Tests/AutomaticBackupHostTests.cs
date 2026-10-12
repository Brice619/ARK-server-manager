using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Core.Tests;

public sealed class AutomaticBackupHostTests
{
    [Fact]
    public async Task ActualHostRetriesSharedClusterBackupWhileAllMapsRemainStopped()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-cluster-backup-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clusterId = Guid.NewGuid();
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var servers = new List<ServerDefinition>();
            for (var index = 0; index < 2; index++)
            {
                var directory = Path.Combine(root, "servers", "map" + index);
                var binary = Path.Combine(directory, "ShooterGame", "Binaries", OperatingSystem.IsWindows() ? "Win64" : "Linux",
                    OperatingSystem.IsWindows() ? "ShooterGameServer.exe" : "ShooterGameServer");
                Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
                await File.WriteAllTextAsync(binary, "stopped fixture; never launched");
                Directory.CreateDirectory(Path.Combine(directory, "ShooterGame", "Saved", "SavedArks"));
                await File.WriteAllTextAsync(Path.Combine(directory, "ShooterGame", "Saved", "SavedArks", "world.ark"), "map world " + index);
                servers.Add(new(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", directory,
                    new() { Name = "Map" + index, GamePort = 7777 + index * 10, QueryPort = 27015 + index * 10,
                        RconPort = 32330 + index * 10 }, clusterId));
            }
            var catalog = await repository.ChangeAsync(c =>
            {
                c.AddCluster(new(clusterId, "Shared fixture", ArkEdition.ASE, new()));
                foreach (var server in servers) c.AddServer(server);
            });
            var storage = ManagedClusterStorage.Prepare(catalog, clusterId, root);
            await new AtomicJsonStore<AutomaticBackupState>(Path.Combine(servers[0].Directory, "backups", "backup-history.json"))
                .SaveAsync(new(1, servers[0].Id, ArkEdition.ASE, TimeSpan.FromHours(3).Ticks, 3, null, [], "previous independent failure"));
            var malformedLegacyPath = Path.Combine(servers[1].Directory, "backups", "backup-history.json");
            Directory.CreateDirectory(Path.GetDirectoryName(malformedLegacyPath)!);
            await File.WriteAllTextAsync(malformedLegacyPath, "{incomplete historical fixture");
            await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player.transfer"), "shared transfer fixture");
            await File.WriteAllTextAsync(Path.Combine(storage.Backups, "hour-00000001.zip"), "expired placeholder");
            await new AtomicJsonStore<AutomaticBackupState>(Path.Combine(storage.Backups, "backup-history.json")).SaveAsync(
                new(1, clusterId, ArkEdition.ASE, TimeSpan.FromHours(11).Ticks, 11, null,
                    [new(1, "hour-00000001.zip", DateTimeOffset.UtcNow.AddHours(-10))], "previous shared failure")
                { Scope = SnapshotScope.SharedCluster });
            await new AtomicJsonStore<CoordinatedBackupSchedule>(Path.Combine(storage.Backups, "Coordinated", "schedule.json")).SaveAsync(
                new(1, clusterId, ArkEdition.ASE, TimeSpan.FromHours(1).Ticks, null, [1], null, "previous coordinated failure", []));
            var hostDirectory = Path.Combine(AppContext.BaseDirectory, "host-fixture");
            var executable = Path.Combine(hostDirectory, OperatingSystem.IsWindows() ? "ArkServerManager.Host.exe" : "ArkServerManager.Host");
            await using var host = ManagedProcess.Start(new(executable, hostDirectory, ["--data-root", root], Path.Combine(root, "host.log")));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            AutomaticBackupState state;
            do
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!host.IsAlive) throw new IOException("Shared backup fixture host exited.");
                state = await AutomaticBackupService.ReadRecordedClusterAsync(catalog, clusterId, root, deadline.Token);
                if (state.PendingHour is not null) await Task.Delay(50, deadline.Token);
            } while (state.PendingHour is not null);
            Assert.Null(state.LastError);
            Assert.Equal(TimeSpan.FromHours(11).Ticks, state.RuntimeTicks);
            Assert.Equal(11, Assert.Single(state.History).RuntimeHour);
            Assert.False(File.Exists(Path.Combine(storage.Backups, "hour-00000001.zip")));
            var manifest = await new SnapshotArchive().VerifyAsync(Path.Combine(storage.Backups, "hour-00000011.zip"));
            Assert.Equal(clusterId, manifest.ServerId); Assert.Equal(SnapshotScope.SharedCluster, manifest.Scope);
            Assert.Equal("player.transfer", Assert.Single(manifest.Files).Path);
            Assert.NotNull(manifest.Capture); Assert.NotEqual(Guid.Empty, manifest.Capture.Id);
            Assert.Equal("shared transfer fixture", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player.transfer")));

            var legacy = await AutomaticBackupService.ReadRecordedAsync(servers[0], deadline.Token);
            Assert.Null(legacy.PendingHour); Assert.Null(legacy.LastError);
            Assert.Equal(TimeSpan.FromHours(3).Ticks, legacy.RuntimeTicks);
            Assert.Equal(3, Assert.Single(legacy.History).RuntimeHour);
            var legacyManifest = await new SnapshotArchive().VerifyAsync(Path.Combine(servers[0].Directory, "backups", "hour-00000003.zip"));
            Assert.Equal(servers[0].Id, legacyManifest.ServerId);
            Assert.Equal(SnapshotLayout.WorldSavedFolders, legacyManifest.Layout);
            Assert.Equal("SavedArks/world.ark", Assert.Single(legacyManifest.Files).Path);
            Assert.Equal("{incomplete historical fixture", await File.ReadAllTextAsync(malformedLegacyPath));
            Assert.Contains("Previous independent backup needs review", await File.ReadAllTextAsync(Path.Combine(root, "logs", "activity.jsonl")));

            var statuses = await new LocalManagerClient(root).CallAsync<List<MemberOperationResult>>("lifecycle-status", null,
                TimeSpan.FromSeconds(2), deadline.Token);
            Assert.Equal(2, statuses.Count);
            Assert.All(statuses, status => { Assert.False(status.Managed); Assert.Equal(ServerState.Stopped, status.State); });
            var reported = await new LocalManagerClient(root).CallAsync<AutomaticBackupState>("cluster-backups-history",
                new ClusterBackupHistoryRequest(clusterId), TimeSpan.FromSeconds(2), deadline.Token);
            Assert.Equal(state.RuntimeTicks, reported.RuntimeTicks);
            Assert.Equal(state.History, reported.History);
            Assert.Equal(clusterId, reported.ServerId);
            Assert.Equal(SnapshotScope.SharedCluster, reported.Scope);
            var client = new LocalManagerClient(root);
            var operationId = Guid.NewGuid();
            var request = new SharedBackupVerificationRequest(clusterId, 11);
            var submitted = await client.CallAsync<BackgroundOperation>("cluster-backup-verify-start", request,
                TimeSpan.FromSeconds(2), deadline.Token, operationId);
            BackgroundOperation completed;
            do
            {
                completed = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(operationId),
                    TimeSpan.FromSeconds(2), deadline.Token);
                if (completed.FinishedAt is null) await Task.Delay(25, deadline.Token);
            } while (completed.FinishedAt is null);
            Assert.Equal(BackgroundOperationState.Succeeded, completed.State);
            var verified = completed.Result!.Value.Deserialize<SharedBackupVerificationResult>()!;
            Assert.Equal(clusterId, verified.ClusterId); Assert.Equal(11, verified.RuntimeHour);
            Assert.Equal(manifest.Capture, verified.Capture); Assert.Equal(1, verified.FileCount);
            var duplicate = await client.CallAsync<BackgroundOperation>("cluster-backup-verify-start", request,
                TimeSpan.FromSeconds(2), deadline.Token, operationId);
            Assert.Equal(submitted.CreatedAt, duplicate.CreatedAt);
            Assert.Equal(BackgroundOperationState.Succeeded, duplicate.State);
            var scheduleStore = new AtomicJsonStore<CoordinatedBackupSchedule>(Path.Combine(storage.Backups, "Coordinated", "schedule.json"));
            CoordinatedBackupSchedule? coordinated;
            do
            {
                deadline.Token.ThrowIfCancellationRequested();
                coordinated = await scheduleStore.LoadAsync(deadline.Token);
                if (coordinated?.PendingHours.Count != 0) await Task.Delay(50, deadline.Token);
            } while (coordinated?.PendingHours.Count != 0);
            Assert.NotNull(coordinated); Assert.Null(coordinated.LastError);
            var point = Assert.Single(coordinated.History); Assert.Equal(1, point.RuntimeHour);
            var coordinatedHistory = await client.CallAsync<CoordinatedBackupHistory>("coordinated-backups-history",
                new CoordinatedBackupHistoryRequest(clusterId), TimeSpan.FromSeconds(2), deadline.Token);
            Assert.Equal(point.Capture, Assert.Single(coordinatedHistory.Points).Capture);
            Assert.Equal(point.Maps, coordinatedHistory.Points[0].Maps);
            foreach (var selectedMap in servers.Select(s => (Guid?)s.Id).Append(null))
            {
                var verifyId = Guid.NewGuid();
                await client.CallAsync<BackgroundOperation>("coordinated-backup-verify-start",
                    new CoordinatedArchiveRequest(clusterId, 1, selectedMap), TimeSpan.FromSeconds(2), deadline.Token, verifyId);
                BackgroundOperation job;
                do
                {
                    job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(verifyId),
                        TimeSpan.FromSeconds(2), deadline.Token);
                    if (job.FinishedAt is null) await Task.Delay(25, deadline.Token);
                } while (job.FinishedAt is null);
                Assert.Equal(BackgroundOperationState.Succeeded, job.State);
                var result = job.Result!.Value.Deserialize<CoordinatedArchiveVerification>()!;
                Assert.Equal(selectedMap, result.ServerId); Assert.Equal(point.Capture, result.Capture);
                Assert.Equal(clusterId, result.ClusterId); Assert.Equal(1, result.FileCount);
            }
            Assert.Equal(TimeSpan.FromHours(1).Ticks, coordinated.RuntimeTicks);
            foreach (var folder in servers.Select(s => Path.Combine(s.Directory, "backups", "Coordinated"))
                .Append(Path.Combine(storage.Backups, "Coordinated")))
            {
                var captured = await new SnapshotArchive().VerifyAsync(Assert.Single(Directory.GetFiles(folder, "*.zip")));
                Assert.Equal(point.Capture, captured.Capture); Assert.Equal(1, captured.RuntimeHour);
            }
            var selectedWorld = Path.Combine(servers[0].Directory, "ShooterGame", "Saved", "SavedArks", "world.ark");
            var siblingWorld = Path.Combine(servers[1].Directory, "ShooterGame", "Saved", "SavedArks", "world.ark");
            await File.WriteAllTextAsync(selectedWorld, "later selected world");
            await File.WriteAllTextAsync(siblingWorld, "later sibling world");
            var config = Path.Combine(servers[0].Directory, "ShooterGame", "Saved", "Config", "fixture.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!); await File.WriteAllTextAsync(config, "current settings");
            var restoreId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("coordinated-world-restore-start",
                new CoordinatedWorldRestoreRequest(clusterId, servers[0].Id, 1, point.Capture.Id, false, true),
                TimeSpan.FromSeconds(2), deadline.Token, restoreId);
            BackgroundOperation restoreJob;
            do
            {
                restoreJob = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(restoreId),
                    TimeSpan.FromSeconds(2), deadline.Token);
                if (restoreJob.FinishedAt is null) await Task.Delay(25, deadline.Token);
            } while (restoreJob.FinishedAt is null);
            Assert.Equal(BackgroundOperationState.Succeeded, restoreJob.State);
            var restored = restoreJob.Result!.Value.Deserialize<WorldRestoreOperationResult>()!;
            Assert.True(restored.Restore.Restored); Assert.False(restored.Restore.Restarted);
            Assert.Equal("map world 0", await File.ReadAllTextAsync(selectedWorld));
            Assert.Equal("later sibling world", await File.ReadAllTextAsync(siblingWorld));
            Assert.Equal("current settings", await File.ReadAllTextAsync(config));
            Assert.Equal("shared transfer fixture", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player.transfer")));
            var interruptedId = Guid.NewGuid();
            var savedDirectory = Path.Combine(servers[0].Directory, "ShooterGame", "Saved");
            var staging = Path.Combine(servers[0].Directory, ".world-restore-stage-" + interruptedId.ToString("N"));
            var previous = Path.Combine(servers[0].Directory, ".world-restore-previous-" + interruptedId.ToString("N"));
            Directory.CreateDirectory(Path.Combine(staging, "SavedArks"));
            Directory.CreateDirectory(Path.Combine(staging, "SaveGames"));
            await File.WriteAllTextAsync(Path.Combine(staging, "SavedArks", "world.ark"), "interrupted replacement");
            Directory.CreateDirectory(previous);
            Directory.Move(Path.Combine(savedDirectory, "SavedArks"), Path.Combine(previous, "SavedArks"));
            await new AtomicJsonStore<WorldRestoreRecord>(Path.Combine(servers[0].Directory, "backups", "restore-transactions", interruptedId.ToString("N") + ".json"))
                .SaveAsync(new(interruptedId, servers[0].Id, ArkEdition.ASE, 1, savedDirectory, staging, previous, WorldRestorePhase.Replacing)
                    { OriginalWorldFolders = ["SavedArks", "SaveGames"] });
            var rollbackId = Guid.NewGuid();
            var rollbackRequest = new WorldRollbackRequest(servers[0].Id, interruptedId, true);
            var rollbackSubmitted = await client.CallAsync<BackgroundOperation>("world-restore-rollback-start", rollbackRequest,
                TimeSpan.FromSeconds(2), deadline.Token, rollbackId);
            BackgroundOperation rollbackJob;
            do
            {
                rollbackJob = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(rollbackId),
                    TimeSpan.FromSeconds(2), deadline.Token);
                if (rollbackJob.FinishedAt is null) await Task.Delay(25, deadline.Token);
            } while (rollbackJob.FinishedAt is null);
            Assert.Equal(BackgroundOperationState.Succeeded, rollbackJob.State);
            Assert.Equal(WorldRestorePhase.RolledBack, rollbackJob.Result!.Value.Deserialize<WorldRollbackResult>()!.Phase);
            Assert.Equal("map world 0", await File.ReadAllTextAsync(selectedWorld));
            Assert.Equal("later sibling world", await File.ReadAllTextAsync(siblingWorld));
            Assert.Equal("current settings", await File.ReadAllTextAsync(config));
            var rollbackDuplicate = await client.CallAsync<BackgroundOperation>("world-restore-rollback-start", rollbackRequest,
                TimeSpan.FromSeconds(2), deadline.Token, rollbackId);
            Assert.Equal(rollbackSubmitted.CreatedAt, rollbackDuplicate.CreatedAt);
            await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player.transfer"), "newer shared state");
            var sharedRestoreId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("shared-transfer-restore-start",
                new SharedRestoreRequest(clusterId, 1, point.Capture.Id, true), TimeSpan.FromSeconds(2), deadline.Token, sharedRestoreId);
            BackgroundOperation sharedJob;
            do
            {
                sharedJob = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(sharedRestoreId),
                    TimeSpan.FromSeconds(2), deadline.Token);
                if (sharedJob.FinishedAt is null) await Task.Delay(25, deadline.Token);
            } while (sharedJob.FinishedAt is null);
            Assert.Equal(BackgroundOperationState.Succeeded, sharedJob.State);
            Assert.Equal(WorldRestorePhase.Replaced, sharedJob.Result!.Value.Deserialize<SharedTransferRestoreRecord>()!.Phase);
            Assert.Equal("shared transfer fixture", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player.transfer")));
            Assert.Equal("newer shared state", await File.ReadAllTextAsync(Path.Combine(storage.Directory,
                ".shared-restore-previous-" + sharedRestoreId.ToString("N"), "player.transfer")));
            var sharedRecords = await client.CallAsync<List<SharedTransferRestoreRecord>>("shared-transfer-restore-records", clusterId,
                TimeSpan.FromSeconds(2), deadline.Token);
            Assert.Equal(sharedRestoreId, Assert.Single(sharedRecords).OperationId);
            var interruptedSharedId = Guid.NewGuid();
            var sharedStage = Path.Combine(storage.Directory, ".shared-restore-stage-" + interruptedSharedId.ToString("N"));
            var sharedPrevious = Path.Combine(storage.Directory, ".shared-restore-previous-" + interruptedSharedId.ToString("N"));
            Directory.CreateDirectory(sharedStage);
            await File.WriteAllTextAsync(Path.Combine(sharedStage, "player.transfer"), "interrupted shared replacement");
            Directory.Move(storage.Transfers, sharedPrevious);
            await new AtomicJsonStore<SharedTransferRestoreRecord>(Path.Combine(storage.Backups, "shared-restore-transactions",
                interruptedSharedId.ToString("N") + ".json")).SaveAsync(
                    new(interruptedSharedId, clusterId, ArkEdition.ASE, 1, true, WorldRestorePhase.Replacing));
            var sharedRollbackId = Guid.NewGuid();
            var sharedRollbackRequest = new SharedRollbackRequest(clusterId, interruptedSharedId, true);
            var submittedSharedRollback = await client.CallAsync<BackgroundOperation>("shared-transfer-rollback-start", sharedRollbackRequest,
                TimeSpan.FromSeconds(2), deadline.Token, sharedRollbackId);
            BackgroundOperation sharedRollbackJob;
            do
            {
                sharedRollbackJob = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(sharedRollbackId),
                    TimeSpan.FromSeconds(2), deadline.Token);
                if (sharedRollbackJob.FinishedAt is null) await Task.Delay(25, deadline.Token);
            } while (sharedRollbackJob.FinishedAt is null);
            Assert.Equal(BackgroundOperationState.Succeeded, sharedRollbackJob.State);
            Assert.Equal(WorldRestorePhase.RolledBack, sharedRollbackJob.Result!.Value.Deserialize<SharedTransferRestoreRecord>()!.Phase);
            Assert.Equal("shared transfer fixture", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player.transfer")));
            Assert.False(Directory.Exists(sharedStage)); Assert.False(Directory.Exists(sharedPrevious));
            var duplicateSharedRollback = await client.CallAsync<BackgroundOperation>("shared-transfer-rollback-start", sharedRollbackRequest,
                TimeSpan.FromSeconds(2), deadline.Token, sharedRollbackId);
            Assert.Equal(submittedSharedRollback.CreatedAt, duplicateSharedRollback.CreatedAt);
            sharedRecords = await client.CallAsync<List<SharedTransferRestoreRecord>>("shared-transfer-restore-records", clusterId,
                TimeSpan.FromSeconds(2), deadline.Token);
            Assert.Equal(WorldRestorePhase.RolledBack, sharedRecords.Single(r => r.OperationId == interruptedSharedId).Phase);
            Assert.Equal("map world 0", await File.ReadAllTextAsync(selectedWorld));
            Assert.Equal("later sibling world", await File.ReadAllTextAsync(siblingWorld));
            Assert.Equal("current settings", await File.ReadAllTextAsync(config));
            var fullRestoreId = Guid.NewGuid();
            var fullRequest = new CoordinatedClusterRestoreRequest(clusterId, 1, point.Capture.Id, true);
            var fullSubmitted = await client.CallAsync<BackgroundOperation>("cluster-world-restore-start", fullRequest,
                TimeSpan.FromSeconds(2), deadline.Token, fullRestoreId);
            BackgroundOperation fullJob;
            do
            {
                fullJob = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(fullRestoreId),
                    TimeSpan.FromSeconds(2), deadline.Token);
                if (fullJob.FinishedAt is null) await Task.Delay(25, deadline.Token);
            } while (fullJob.FinishedAt is null);
            Assert.Equal(BackgroundOperationState.Succeeded, fullJob.State);
            Assert.Equal(ClusterRestorePhase.Restored, fullJob.Result!.Value.Deserialize<ClusterRestoreRecord>()!.Phase);
            Assert.Equal("map world 0", await File.ReadAllTextAsync(selectedWorld));
            Assert.Equal("map world 1", await File.ReadAllTextAsync(siblingWorld));
            Assert.Equal("current settings", await File.ReadAllTextAsync(config));
            Assert.Equal("shared transfer fixture", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player.transfer")));
            var fullDuplicate = await client.CallAsync<BackgroundOperation>("cluster-world-restore-start", fullRequest,
                TimeSpan.FromSeconds(2), deadline.Token, fullRestoreId);
            Assert.Equal(fullSubmitted.CreatedAt, fullDuplicate.CreatedAt);
            var fullRecords = await client.CallAsync<List<ClusterRestoreRecord>>("cluster-restore-records", clusterId,
                TimeSpan.FromSeconds(2), deadline.Token);
            Assert.Equal(fullRestoreId, Assert.Single(fullRecords).OperationId);
            var interruptedClusterId = Guid.NewGuid();
            var interruptedClusterPrevious = Path.Combine(servers[0].Directory, ".cluster-restore-previous-" + interruptedClusterId.ToString("N"));
            Directory.CreateDirectory(interruptedClusterPrevious);
            Directory.Move(Path.Combine(servers[0].Directory, "ShooterGame", "Saved", "SavedArks"), Path.Combine(interruptedClusterPrevious, "SavedArks"));
            var interruptedClusterStage = Path.Combine(servers[0].Directory, ".cluster-restore-stage-" + interruptedClusterId.ToString("N"));
            Directory.CreateDirectory(Path.Combine(interruptedClusterStage, "SavedArks"));
            await File.WriteAllTextAsync(Path.Combine(interruptedClusterStage, "SavedArks", "world.ark"), "partial group payload");
            await new AtomicJsonStore<ClusterRestoreRecord>(Path.Combine(storage.Backups, "cluster-restore-transactions", interruptedClusterId.ToString("N") + ".json"))
                .SaveAsync(new(1, interruptedClusterId, clusterId, ArkEdition.ASE, 1, point.Capture, point.Maps, ClusterRestorePhase.Replacing)
                {
                    OriginalFolders = servers.SelectMany(s => new[] { new ClusterOriginalFolder(s.Id, "SavedArks", true),
                        new ClusterOriginalFolder(s.Id, "SaveGames", true) }).Append(new(null, "Transfers", true)).ToList()
                });
            var clusterRecoveryId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("cluster-restore-recovery-start",
                new ClusterRestoreRecoveryRequest(clusterId, interruptedClusterId, true), TimeSpan.FromSeconds(2), deadline.Token, clusterRecoveryId);
            BackgroundOperation clusterRecoveryJob;
            do
            {
                clusterRecoveryJob = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(clusterRecoveryId),
                    TimeSpan.FromSeconds(2), deadline.Token);
                if (clusterRecoveryJob.FinishedAt is null) await Task.Delay(25, deadline.Token);
            } while (clusterRecoveryJob.FinishedAt is null);
            Assert.Equal(BackgroundOperationState.Succeeded, clusterRecoveryJob.State);
            Assert.Equal(ClusterRestorePhase.RolledBack, clusterRecoveryJob.Result!.Value.Deserialize<ClusterRestoreRecord>()!.Phase);
            Assert.False(Directory.Exists(interruptedClusterStage)); Assert.False(Directory.Exists(interruptedClusterPrevious));
            Assert.Equal("map world 0", await File.ReadAllTextAsync(selectedWorld));
            Assert.Equal("map world 1", await File.ReadAllTextAsync(siblingWorld));
            Assert.Equal("current settings", await File.ReadAllTextAsync(config));
            Assert.Equal("shared transfer fixture", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player.transfer")));
        }
        catch (Exception error)
        {
            // Preserve the checkpoint and Host evidence in the failure before removing this private fixture.
            var evidence = new List<string>();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) is "backup-history.json" or "schedule.json" or "host.log" or "activity.jsonl"))
            {
                try
                {
                    var contents = await File.ReadAllTextAsync(file);
                    evidence.Add(Path.GetRelativePath(root, file) + "\n" + contents[..Math.Min(contents.Length, 16000)]);
                }
                catch (IOException readError) { evidence.Add(Path.GetRelativePath(root, file) + ": " + readError.Message); }
            }
            var diagnostic = "Shared Host fixture failed. Recorded evidence:\n" + string.Join("\n", evidence);
            var diagnosticFolder = Path.Combine(AppContext.BaseDirectory, "failure-evidence");
            Directory.CreateDirectory(diagnosticFolder);
            await File.WriteAllTextAsync(Path.Combine(diagnosticFolder, "shared-host-" + Guid.NewGuid().ToString("N") + ".txt"),
                diagnostic + "\n" + error);
            throw new InvalidOperationException(diagnostic, error);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ActualHostRetriesStoppedWorldBackupAndExpiresByRecordedRuntime()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-backup-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var directory = Path.Combine(root, "servers", "fixture");
            var platform = OperatingSystem.IsWindows() ? "Windows" : "Linux";
            var binary = Path.Combine(directory, "ShooterGame", "Binaries", platform == "Windows" ? "Win64" : "Linux",
                platform == "Windows" ? "ShooterGameServer.exe" : "ShooterGameServer");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            await File.WriteAllTextAsync(binary, "native-shaped stopped fixture; never launched");
            var saved = Path.Combine(directory, "ShooterGame", "Saved");
            Directory.CreateDirectory(Path.Combine(saved, "SavedArks"));
            Directory.CreateDirectory(Path.Combine(saved, "SaveGames"));
            Directory.CreateDirectory(Path.Combine(saved, "Config", platform + "Server"));
            await File.WriteAllTextAsync(Path.Combine(saved, "SavedArks", "TheIsland.ark"), "world fixture");
            await File.WriteAllTextAsync(Path.Combine(saved, "SaveGames", "player.arkprofile"), "player fixture");
            var config = Path.Combine(saved, "Config", platform + "Server", "GameUserSettings.ini");
            await File.WriteAllTextAsync(config, "[ServerSettings]\nServerAdminPassword=private-fixture\n");
            var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", directory,
                new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
            await new CatalogRepository(Path.Combine(root, "catalog.json")).ChangeAsync(c => c.AddServer(server));
            var backups = Path.Combine(directory, "backups"); Directory.CreateDirectory(backups);
            await File.WriteAllTextAsync(Path.Combine(backups, "hour-00000001.zip"), "expired placeholder");
            await new AtomicJsonStore<AutomaticBackupState>(Path.Combine(backups, "backup-history.json")).SaveAsync(
                new(1, server.Id, server.Edition, TimeSpan.FromHours(11).Ticks, 11, null,
                    [new(1, "hour-00000001.zip", DateTimeOffset.UtcNow.AddHours(-10))], "previous failure"));
            var hostDirectory = Path.Combine(AppContext.BaseDirectory, "host-fixture");
            var executable = Path.Combine(hostDirectory, OperatingSystem.IsWindows() ? "ArkServerManager.Host.exe" : "ArkServerManager.Host");
            await using var host = ManagedProcess.Start(new(executable, hostDirectory, ["--data-root", root], Path.Combine(root, "host.log")));
            var client = new LocalManagerClient(root);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            AutomaticBackupState? completed = null;
            while (completed?.PendingHour is not null || completed is null)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    completed = await client.CallAsync<AutomaticBackupState>("backups-history", new BackupHistoryRequest(server.Id),
                        TimeSpan.FromSeconds(1), deadline.Token);
                }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException && !deadline.IsCancellationRequested)
                { if (!host.IsAlive) throw new IOException("Backup fixture host exited."); }
                if (completed?.PendingHour is not null || completed is null) await Task.Delay(50, deadline.Token);
            }
            Assert.Null(completed.LastError);
            Assert.Equal(TimeSpan.FromHours(11).Ticks, completed.RuntimeTicks);
            Assert.Equal(11, Assert.Single(completed.History).RuntimeHour);
            Assert.False(File.Exists(Path.Combine(backups, "hour-00000001.zip")));
            var manifest = await new SnapshotArchive().VerifyAsync(Path.Combine(backups, "hour-00000011.zip"));
            Assert.Equal(SnapshotLayout.WorldSavedFolders, manifest.Layout);
            Assert.Equal(2, manifest.Files.Count);
            Assert.DoesNotContain(manifest.Files, f => f.Path.StartsWith("Config/"));
            Assert.Contains("private-fixture", await File.ReadAllTextAsync(config));
            var statuses = await client.CallAsync<List<MemberOperationResult>>("lifecycle-status", null,
                TimeSpan.FromSeconds(2), deadline.Token);
            Assert.False(Assert.Single(statuses).Managed);
        }
        finally { Directory.Delete(root, true); }
    }
}
