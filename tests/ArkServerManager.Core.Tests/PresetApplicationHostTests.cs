using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Core.Tests;

public sealed class PresetApplicationHostTests
{
    [Theory]
    [InlineData(SaveChoice.SaveForNextStart)]
    [InlineData(SaveChoice.SaveAndRestart)]
    public async Task ActualHostReviewsAndAppliesPresetToStoppedServerWithoutLaunchingIt(SaveChoice choice)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-preset-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var directory = Path.Combine(root, "servers", "fixture");
            var platform = OperatingSystem.IsWindows() ? "Windows" : "Linux";
            var binary = Path.Combine(directory, "ShooterGame", "Binaries", platform == "Windows" ? "Win64" : "Linux",
                platform == "Windows" ? "ShooterGameServer.exe" : "ShooterGameServer");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            await File.WriteAllTextAsync(binary, "never launched; not an executable");
            var config = Path.Combine(directory, "ShooterGame", "Saved", "Config", platform + "Server");
            Directory.CreateDirectory(config);
            var userPath = Path.Combine(config, "GameUserSettings.ini");
            await File.WriteAllTextAsync(userPath, "; keep comment\n[ServerSettings]\nServerAdminPassword=fixture-private\nServerPassword=fixture-join\nRCONEnabled=True\nRCONPort=32330\n");
            var id = Guid.NewGuid();
            var settings = new ServerSettings { Name = "Fixture", MaxPlayers = 20, GamePort = 7777, QueryPort = 27015, RconPort = 32330 };
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var clusterId = Guid.NewGuid();
            await repository.ChangeAsync(c => { c.AddCluster(new(clusterId, "Fixture cluster", ArkEdition.ASE, settings)); c.AddServer(new(id, ArkEdition.ASE, "TheIsland", directory, settings, clusterId)); });
            var library = new PresetLibrary(Path.Combine(root, "app-settings", "presets.json"));
            var preset = await library.SaveAsync(new("Relaxed", ArkEdition.ASE, settings with { Name = "Wrong identity", MaxPlayers = 40 }, [])
            {
                UserSettings = "[ServerSettings]\nTamingSpeedMultiplier=5\n",
                GameSettings = "[/Script/ShooterGame.ShooterGameMode]\nRepeated=one\nRepeated=two\n"
            });
            var hostDirectory = Path.Combine(AppContext.BaseDirectory, "host-fixture");
            var executable = Path.Combine(hostDirectory, OperatingSystem.IsWindows() ? "ArkServerManager.Host.exe" : "ArkServerManager.Host");
            await using var host = ManagedProcess.Start(new(executable, hostDirectory, ["--data-root", root], Path.Combine(root, "host.log")));
            var client = new LocalManagerClient(root);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (true)
            {
                try { await client.CallAsync<JsonElement>("ping", null, TimeSpan.FromMilliseconds(300), deadline.Token); break; }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException && !deadline.IsCancellationRequested)
                { if (!host.IsAlive) throw new IOException("Fixture host exited."); await Task.Delay(50, deadline.Token); }
            }
            var review = await client.CallAsync<PresetApplicationReview>("presets-application-review", new PresetApplicationSelection(id, preset.Id),
                TimeSpan.FromSeconds(8), deadline.Token);
            Assert.Equal(40, review.DesiredMaxPlayers);
            Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(review));
            Assert.False(File.Exists(Path.Combine(config, "Game.ini")));
            var requestId = Guid.NewGuid();
            var edit = new PresetApplicationSave(review, choice, true);
            var started = await client.CallAsync<BackgroundOperation>("presets-application-save-start", edit,
                TimeSpan.FromSeconds(8), deadline.Token, requestId);
            var completed = await FinishAsync(requestId);
            Assert.Equal(BackgroundOperationState.Succeeded, completed.State);
            var result = completed.Result!.Value.Deserialize<PresetApplicationSaveResult>()!;
            Assert.True(result.Save.Saved); Assert.False(result.Save.Pending); Assert.False(result.Save.Restarted);
            Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(completed));
            var saved = (await repository.ReadAsync()).Servers.Single();
            Assert.Equal("Fixture", saved.Settings.Name); Assert.Equal(40, saved.Settings.MaxPlayers);
            Assert.Equal("5", new IniDocument(await File.ReadAllTextAsync(userPath)).GetValues("ServerSettings", "TamingSpeedMultiplier").Single());
            Assert.Contains("ServerAdminPassword=fixture-private", await File.ReadAllTextAsync(userPath));
            Assert.Contains("ServerPassword=fixture-join", await File.ReadAllTextAsync(userPath));
            Assert.Equal(["one", "two"], new IniDocument(await File.ReadAllTextAsync(Path.Combine(config, "Game.ini")))
                .GetValues("/Script/ShooterGame.ShooterGameMode", "Repeated"));
            var duplicate = await client.CallAsync<BackgroundOperation>("presets-application-save-start", edit,
                TimeSpan.FromSeconds(8), deadline.Token, requestId);
            Assert.Equal(started.CreatedAt, duplicate.CreatedAt); Assert.Equal(BackgroundOperationState.Succeeded, duplicate.State);
            var staleId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("presets-application-save-start", edit, TimeSpan.FromSeconds(8), deadline.Token, staleId);
            Assert.Equal(BackgroundOperationState.Failed, (await FinishAsync(staleId)).State);
            var statuses = await client.CallAsync<List<MemberOperationResult>>("lifecycle-status", null, TimeSpan.FromSeconds(8), deadline.Token);
            Assert.False(Assert.Single(statuses).Managed);
            Assert.Equal(ServerState.Stopped, statuses.Single().State);
            // Seed a next-start queue entry, then exercise its public redacted status
            // and confirmed cancellation through the actual running Host.
            var queued = await new PresetApplicationReviewService(repository, library).PrepareAsync(new(id, preset.Id));
            var queue = new PresetApplicationQueue(repository, Path.Combine(root, "app-settings", "pending-presets.json"),
                (server, ct) => throw new InvalidOperationException("Not applying this seeded queue."),
                (server, removed, ct) => throw new InvalidOperationException("No cleanup."));
            await queue.EnqueueAsync(queued);
            var pending = await client.CallAsync<PresetApplicationPendingResult>("presets-application-pending", id, TimeSpan.FromSeconds(8), deadline.Token);
            Assert.Equal(id, pending.ServerId); Assert.NotNull(pending.Pending);
            Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(pending));
            var beforeCancel = await File.ReadAllBytesAsync(userPath);
            var discardId = Guid.NewGuid();
            var discard = new PresetApplicationDiscard(id, pending.Pending!.Review.ReviewToken, true);
            var discardStarted = await client.CallAsync<BackgroundOperation>("presets-application-discard-start", discard,
                TimeSpan.FromSeconds(8), deadline.Token, discardId);
            var discarded = await FinishAsync(discardId);
            Assert.Equal(BackgroundOperationState.Succeeded, discarded.State);
            Assert.True(discarded.Result!.Value.Deserialize<PresetApplicationDiscardResult>()!.Discarded);
            Assert.Equal(beforeCancel, await File.ReadAllBytesAsync(userPath));
            Assert.Null((await client.CallAsync<PresetApplicationPendingResult>("presets-application-pending", id, TimeSpan.FromSeconds(8), deadline.Token)).Pending);
            Assert.Equal(discardStarted.CreatedAt, (await client.CallAsync<BackgroundOperation>("presets-application-discard-start", discard,
                TimeSpan.FromSeconds(8), deadline.Token, discardId)).CreatedAt);
            var overrideReview = await client.CallAsync<ClusterOverrideReview>("cluster-overrides-read", id, TimeSpan.FromSeconds(8), deadline.Token);
            var overrideId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("cluster-overrides-save-start", new ClusterOverrideSave(overrideReview,
                [new("GameUserSettings.ini", "ServerSettings", "TamingSpeedMultiplier")], true, false, true), TimeSpan.FromSeconds(8), deadline.Token, overrideId);
            Assert.Equal(BackgroundOperationState.Succeeded, (await FinishAsync(overrideId)).State);
            var sharedDraft = new ServerPreset("Direct shared edit", ArkEdition.ASE, settings with { MaxPlayers = 70 }, [])
            { UserSettings = "[ServerSettings]\nTamingSpeedMultiplier=8\nXPMultiplier=3\nServerAdminPassword=must-not-appear\n" };
            var sharedReview = await client.CallAsync<ClusterPresetReview>("cluster-preset-review", new ClusterPresetSelection(clusterId, Guid.NewGuid(), [id])
                { DraftPreset = sharedDraft }, TimeSpan.FromSeconds(8), deadline.Token);
            Assert.DoesNotContain("must-not-appear", JsonSerializer.Serialize(sharedReview));
            Assert.Equal(40, sharedReview.Maps.Single().DesiredMaxPlayers);
            var sharedId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("cluster-preset-save-start", new ClusterPresetSave(sharedReview, choice, true) { UseForNewMaps = true },
                TimeSpan.FromSeconds(8), deadline.Token, sharedId);
            var sharedResult = await FinishAsync(sharedId); Assert.Equal(BackgroundOperationState.Succeeded, sharedResult.State);
            Assert.All(sharedResult.Result!.Value.Deserialize<ClusterPresetSaveResult>()!.Maps, map => Assert.Null(map.Error));
            var currentUser = new IniDocument(await File.ReadAllTextAsync(userPath));
            Assert.Equal(["5"], currentUser.GetValues("ServerSettings", "TamingSpeedMultiplier"));
            Assert.Equal(["3"], currentUser.GetValues("ServerSettings", "XPMultiplier"));
            Assert.Equal(["fixture-private"], currentUser.GetValues("ServerSettings", "ServerAdminPassword"));
            var sharedDefaults = (await repository.ReadAsync()).Clusters.Single().Defaults!;
            Assert.Equal(70, sharedDefaults.Settings.MaxPlayers);
            Assert.Equal(["8"], new IniDocument(sharedDefaults.UserSettings!).GetValues("ServerSettings", "TamingSpeedMultiplier"));
            var resetReview = await client.CallAsync<ClusterOverrideReview>("cluster-overrides-read", id, TimeSpan.FromSeconds(8), deadline.Token);
            await repository.ChangeAsync(c => c.UpdateClusterDefaults(clusterId, "Changed shared settings", 75));
            var staleResetId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("cluster-overrides-save-start", new ClusterOverrideSave(resetReview, [], false, false, true) { Choice = choice },
                TimeSpan.FromSeconds(8), deadline.Token, staleResetId);
            Assert.Equal(BackgroundOperationState.Failed, (await FinishAsync(staleResetId)).State);
            Assert.Equal(["5"], new IniDocument(await File.ReadAllTextAsync(userPath)).GetValues("ServerSettings", "TamingSpeedMultiplier"));
            Assert.True((await repository.ReadAsync()).Servers.Single().OverrideClusterMaxPlayers);
            await repository.ChangeAsync(c => c.UpdateClusterDefaults(clusterId, "Fixture cluster", 70));
            resetReview = await client.CallAsync<ClusterOverrideReview>("cluster-overrides-read", id, TimeSpan.FromSeconds(8), deadline.Token);
            var resetId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("cluster-overrides-save-start", new ClusterOverrideSave(resetReview, [], false, false, true) { Choice = choice },
                TimeSpan.FromSeconds(8), deadline.Token, resetId);
            var resetJob = await FinishAsync(resetId); Assert.Equal(BackgroundOperationState.Succeeded, resetJob.State);
            var reset = resetJob.Result!.Value.Deserialize<ClusterOverrideSaveResult>()!;
            Assert.Null(reset.ApplyError); Assert.NotNull(reset.Applied); Assert.True(reset.Applied.Save.Saved); Assert.False(reset.Applied.Save.Pending);
            var resetUser = new IniDocument(await File.ReadAllTextAsync(userPath));
            Assert.Equal(["8"], resetUser.GetValues("ServerSettings", "TamingSpeedMultiplier"));
            Assert.Equal(["3"], resetUser.GetValues("ServerSettings", "XPMultiplier"));
            Assert.Equal(["fixture-private"], resetUser.GetValues("ServerSettings", "ServerAdminPassword"));
            var inherited = (await repository.ReadAsync()).Servers.Single();
            Assert.Equal(70, inherited.Settings.MaxPlayers); Assert.False(inherited.OverrideClusterMaxPlayers); Assert.Empty(inherited.ClusterOverrides);
            var rawSource = await client.CallAsync<ServerConfigurationFile>("server-configuration-read", new ServerConfigurationReadRequest(id, "GameUserSettings.ini"), TimeSpan.FromSeconds(8), deadline.Token);
            var rawFile = Path.Combine(root, "app-settings", "pending-configuration.json");
            var rawQueue = new PendingConfigurationEdits(repository, rawFile, (_, _) => throw new InvalidOperationException("Seeding must not apply configuration."));
            var rawId = Guid.NewGuid();
            var rawDesired = rawSource.Text!.Replace("XPMultiplier=3", "XPMultiplier=4");
            await rawQueue.EnqueueAsync(rawId, new(id, "GameUserSettings.ini", rawSource.SourceSHA256!, rawDesired));
            var rawStore = new AtomicJsonStore<PendingConfigurationDocument>(rawFile, true);
            var rawDocument = (await rawStore.LoadAsync())!;
            await rawStore.SaveAsync(rawDocument with { Changes = rawDocument.Changes.Select(p => p with { Started = true }).ToList() });
            await File.WriteAllTextAsync(userPath, rawSource.Text.Replace("XPMultiplier=3", "XPMultiplier=9"));
            var resolutionReview = await client.CallAsync<PendingConfigurationResolutionReview>("server-configuration-resolution-review",
                new PendingConfigurationReviewRequest(id, rawId), TimeSpan.FromSeconds(8), deadline.Token);
            Assert.True(resolutionReview.Started); Assert.Contains("XPMultiplier=9", resolutionReview.CurrentText);
            var resolveChoice = choice == SaveChoice.SaveForNextStart ? ConfigurationResolutionChoice.UseSavedText : ConfigurationResolutionChoice.KeepCurrentFile;
            await File.WriteAllTextAsync(userPath, rawSource.Text.Replace("XPMultiplier=3", "XPMultiplier=10"));
            var staleResolution = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("server-configuration-resolve-start", new PendingConfigurationResolution(id, rawId, resolutionReview.Token, resolveChoice, true),
                TimeSpan.FromSeconds(8), deadline.Token, staleResolution);
            Assert.Equal(BackgroundOperationState.Failed, (await FinishAsync(staleResolution)).State);
            resolutionReview = await client.CallAsync<PendingConfigurationResolutionReview>("server-configuration-resolution-review",
                new PendingConfigurationReviewRequest(id, rawId), TimeSpan.FromSeconds(8), deadline.Token);
            var resolutionId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("server-configuration-resolve-start", new PendingConfigurationResolution(id, rawId, resolutionReview.Token, resolveChoice, true),
                TimeSpan.FromSeconds(8), deadline.Token, resolutionId);
            var resolved = await FinishAsync(resolutionId); Assert.Equal(BackgroundOperationState.Succeeded, resolved.State);
            var resolution = resolved.Result!.Value.Deserialize<PendingConfigurationResolutionResult>()!;
            Assert.Equal(resolveChoice == ConfigurationResolutionChoice.UseSavedText, resolution.Applied);
            Assert.Equal(resolveChoice == ConfigurationResolutionChoice.KeepCurrentFile, resolution.KeptCurrent);
            Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(resolved));
            Assert.Empty(await client.CallAsync<List<PendingConfigurationStatus>>("server-configuration-pending", id, TimeSpan.FromSeconds(8), deadline.Token));
            Assert.Equal([resolveChoice == ConfigurationResolutionChoice.UseSavedText ? "4" : "10"], new IniDocument(await File.ReadAllTextAsync(userPath)).GetValues("ServerSettings", "XPMultiplier"));
            var beforeMissing = await client.CallAsync<ServerConfigurationFile>("server-configuration-read", new ServerConfigurationReadRequest(id, "GameUserSettings.ini"), TimeSpan.FromSeconds(8), deadline.Token);
            var missingId = Guid.NewGuid();
            var missingDesired = beforeMissing.Text + "\n[RecoveredFixture]\nOption=restored\n";
            await rawQueue.EnqueueAsync(missingId, new(id, "GameUserSettings.ini", beforeMissing.SourceSHA256!, missingDesired));
            rawDocument = (await rawStore.LoadAsync())!;
            await rawStore.SaveAsync(rawDocument with { Changes = rawDocument.Changes.Select(p => p with { Started = true }).ToList() });
            File.Delete(userPath);
            var missingReview = await client.CallAsync<PendingConfigurationResolutionReview>("server-configuration-resolution-review",
                new PendingConfigurationReviewRequest(id, missingId), TimeSpan.FromSeconds(8), deadline.Token);
            Assert.True(missingReview.CurrentMissing); Assert.Null(missingReview.CurrentSourceSHA256);
            var missingRestoreId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("server-configuration-resolve-start", new PendingConfigurationResolution(id, missingId,
                missingReview.Token, ConfigurationResolutionChoice.UseSavedText, true), TimeSpan.FromSeconds(8), deadline.Token, missingRestoreId);
            var missingRestored = await FinishAsync(missingRestoreId); Assert.Equal(BackgroundOperationState.Succeeded, missingRestored.State);
            Assert.Equal(missingDesired, await File.ReadAllTextAsync(userPath));
            Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(missingRestored));
            Assert.Empty(await client.CallAsync<List<PendingConfigurationStatus>>("server-configuration-pending", id, TimeSpan.FromSeconds(8), deadline.Token));

            async Task<BackgroundOperation> FinishAsync(Guid operation)
            {
                while (true)
                {
                    var snapshot = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(operation),
                        TimeSpan.FromSeconds(8), deadline.Token);
                    if (snapshot.State is BackgroundOperationState.Succeeded or BackgroundOperationState.Failed or BackgroundOperationState.Canceled) return snapshot;
                    await Task.Delay(50, deadline.Token);
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
