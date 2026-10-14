using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    /// <summary>Offscreen review fixtures use actual production controls with no manager connection.</summary>
    public IReadOnlyList<SettingsBindingReview> CaptureSettingsBindingsForReview()
    {
        if(!designPreview)throw new InvalidOperationException("Use the isolated review fixture.");var result=new List<SettingsBindingReview>();var previous=settingsCategory;
        for(var category=0;category<10;category++){settingsCategory=category;Render();result.AddRange(settingsBindingReview);}settingsCategory=previous;Render();return result.Distinct().ToList();
    }
    public IReadOnlyList<SettingsBindingReview> CurrentSettingsBindingsForReview()=>designPreview?settingsBindingReview.ToArray():throw new InvalidOperationException();
    public string? SavedSettingForReview(int file,string section,string key)=>designPreview?new IniDocument(advancedLoadedFiles.GetValueOrDefault((ReviewServerId,file))?.Text??"").GetValues(section,key).LastOrDefault():throw new InvalidOperationException();
    public IReadOnlyList<string> SavedSettingValuesForReview(int file,string section,string key)=>designPreview?new IniDocument(advancedLoadedFiles.GetValueOrDefault((ReviewServerId,file))?.Text??"").GetValues(section,key):throw new InvalidOperationException();
    public ServerLaunchPreferences SavedLaunchForReview()=>designPreview?loadedLaunchPreferences[ReviewServerId].Preferences:throw new InvalidOperationException();
    public void SeedCreatureOverrideForReview()
    {
        if(!designPreview)throw new InvalidOperationException();var loaded=advancedLoadedFiles[(ReviewServerId,1)];advancedLoadedFiles[(ReviewServerId,1)]=loaded with {Text=SettingsDraft.Apply(loaded.Text??"",[new(GameSection,"DinoSpawnWeightMultipliers",["(DinoNameTag=Rex,SpawnWeightMultiplier=1.0,OverrideSpawnLimitPercentage=True,SpawnLimitPercentage=0.2)"])])};creatureClassBindings[ReviewServerId]=new(){["Rex"]="Rex_Character_BP_C"};
    }
    public static MainWindow CreateDesignReview(string screen)
    {
        var window = new MainWindow(["--design-preview"]);
        if(screen=="approved-management-actions")
        {window.reviewServerVisible=true;window.selectedServer=ReviewServerId;window.managementTab=3;window.Render();return window;}
        if(screen is "approved-backups" or "approved-players" or "approved-console")
        {window.reviewServerVisible=true;window.selectedServer=ReviewServerId;window.managementTab=screen=="approved-backups"?3:screen=="approved-players"?4:5;window.SeedApprovedManagementReview();window.Render();return window;}
        if(screen.StartsWith("approved-settings-",StringComparison.Ordinal)&&int.TryParse(screen[18..],out var category)){window.reviewServerVisible=true;window.selectedServer=ReviewServerId;window.managementTab=1;window.settingsCategory=category;window.Render();return window;}
        if (screen is "approved-settings" or "approved-mods" or "approved-editor-actions" or "approved-settings-bindings" or "approved-engram-actions" or "approved-creature-actions") { window.reviewServerVisible = true; window.selectedServer = ReviewServerId; window.managementTab = screen == "approved-mods" ? 2 : 1; window.settingsCategory=screen=="approved-engram-actions"?7:screen=="approved-creature-actions"?3:0;window.Render(); return window; }
        if (screen is "approved-solo" or "approved-solo-actions") { window.reviewServerVisible = true; window.selectedServer = ReviewServerId; window.Render(); return window; }
        if (screen is "approved-cluster" or "approved-cluster-actions" or "approved-cluster-activity" or "approved-cluster-activity-actions" or "approved-cluster-transfers" or "approved-cluster-transfers-actions" or "approved-cluster-backups" or "approved-cluster-backups-actions")
        {
            var cluster = window.catalog!.Clusters[0];
            window.catalog.Clusters[0] = cluster with { Name = "Explorer Cluster", Settings = cluster.Settings with { MaxPlayers = 5, Values = new() { ["ServerPVE"] = "True" } } };
            var members = window.catalog.Servers.Where(s => s.ClusterId == cluster.Id).ToArray();
            var maps = new[] { "TheIsland", "ScorchedEarth", "Aberration", "Ragnarok" };
            for (var i = 0; i < members.Length; i++) { var index = window.catalog.Servers.IndexOf(members[i]); window.catalog.Servers[index] = members[i] with { Map = maps[i], Settings = members[i].Settings with { MaxPlayers = 5 } }; }
            if(screen is "approved-cluster-backups" or "approved-cluster-backups-actions" or "approved-cluster-transfers" or "approved-cluster-transfers-actions" or "approved-cluster-activity" or "approved-cluster-activity-actions")window.catalog.Servers.Add(members[0] with{Id=Guid.NewGuid(),Map="Fjordur",Directory=members[0].Directory+"-fjordur",Settings=members[0].Settings.Copy() with{GamePort=7877,QueryPort=27115,RconPort=32430}});
            window.overviewPreviewRunning.Add(members[0].Id); window.selectedCluster = cluster.Id;
            if(screen.StartsWith("approved-cluster-activity")){window.managementTab=6;window.SeedApprovedClusterActivityReview(window.catalog.Clusters[0],window.catalog.Servers.Where(s=>s.ClusterId==cluster.Id).ToArray());}
            if(screen.StartsWith("approved-cluster-transfers"))window.managementTab=1;
            if(screen.StartsWith("approved-cluster-backups")){window.managementTab=3;window.SeedApprovedClusterBackupsReview(window.catalog.Clusters[0],window.catalog.Servers.Where(s=>s.ClusterId==cluster.Id).ToArray());}
            window.Render(); return window;
        }
        if (screen == "dashboard") { window.Render(); return window; }
        if (screen == "dashboard-fit-actions")
        {
            var solo=window.catalog!.Servers.First(s=>s.ClusterId is null);var cluster=window.catalog.Clusters[0];
            for(var i=0;i<6;i++){window.catalog.Servers.Add(solo with {Id=Guid.NewGuid()});window.catalog.Clusters.Add(cluster with {Id=Guid.NewGuid()});}
            window.Render();return window;
        }
        if (screen == "startup-settings") { window.settingsPage = true; window.applicationCategory = 1; window.Render(); return window; }
        if (screen is "notification-indicator" or "window-controls")
        {
            window.newActivityCount = 3; window.UpdateActivityMenu();
            window.Render(); return window;
        }
        if (screen == "notification-settings")
        {
            window.settingsPage = true; window.applicationCategory = 2;
            window.Render(); return window;
        }
        if (screen is "map-picker" or "custom-map-picker")
        {
            window.ResetCreationDraft(); window.creationPage = true; window.creationStep = 2;
            window.draftEdition = ArkEdition.ASA; window.draftCluster = false;
            window.draftCustomMap = screen == "custom-map-picker";
            window.Render(); return window;
        }
        if (screen is "network-settings" or "network-connections")
        {
            var server = window.catalog!.Servers.First();
            window.selectedServer = server.Id; window.managementTab = 1; window.settingsCategory = 1;
            var values = BasicSettingsValues.From(server.Settings);
            window.basicEditors[server.Id] = new BasicEditor { Draft = values,
                Review = new(server.Id, server.Directory, server.Edition, server.Map, server.ClusterId, values),
                Notice = "Illustrative network settings · No changes are saved." };
            window.localNetworkReport = new(DateTimeOffset.UtcNow, [new("Ethernet", "192.168.1.25")], []);
            window.networkAddressNotice = "Illustrative connection address";
            window.Render(); return window;
        }
        if (screen == "cluster-defaults")
        {
            var cluster = window.catalog!.Clusters.First();
            window.selectedCluster = cluster.Id; window.managementTab = 1;
            window.clusterDefaultEditors[cluster.Id] = new ClusterDefaultEditor { Review = new(cluster.Id, cluster.Name, cluster.Edition, cluster.Settings.MaxPlayers),
                Name = cluster.Name, Capacity = cluster.Settings.MaxPlayers.ToString(), Notice = "Illustrative defaults · No changes are saved." };
            window.Render(); return window;
        }
        if (screen == "restart-schedule")
        {
            var server = window.catalog!.Servers.First();
            window.selectedServer = server.Id; window.scheduleServer = server.Id;
            window.scheduleEditors[server.Id] = new ScheduleEditor { Loaded = true, Enabled = true, Time = "04:00", Notice = "Illustrative schedule · Local computer time." };
            window.Render(); return window;
        }
        if (screen == "server-overview")
        {
            window.selectedServer = window.catalog!.Servers.First().Id;
            window.managementTab = 0;
            window.Render(); return window;
        }
        if (screen is "access-settings" or "access-queued")
        {
            var server = window.catalog!.Servers.First();
            window.selectedServer = server.Id; window.managementTab = 1; window.settingsCategory = 2;
            window.accessEditors[server.Id] = new AccessEditor { Review = new(server.Id, server.Directory, server.Edition, server.Map, server.ClusterId,
                new string('A', 64), true, true, screen == "access-queued" ? Guid.NewGuid() : null, false),
                Notice = "Changes apply only to this map. Existing passwords are kept unless you replace or remove them." };
            window.Render(); return window;
        }
        if (screen is "preset-application" or "preset-application-picker" or "preset-queued" or "preset-interrupted")
        {
            var server = window.catalog!.Servers.First();
            var presetId = Guid.NewGuid();
            window.selectedServer = server.Id; window.presetApplicationServer = server.Id;
            window.applicationPresets = [new(presetId, new("Relaxed PvE", server.Edition, server.Settings.Copy(), ["731604991"]))];
            window.applicationPresetId = presetId;
            if (screen != "preset-application-picker")
                window.applicationPresetReview = new(server.Id, presetId, server.Settings.Name, "Relaxed PvE", server.Edition,
                    server.Settings.MaxPlayers, 40, ["731604991"], server.Edition == ArkEdition.ASE ? ["924619115"] : [], 12, new string('A', 64));
            if (screen is "preset-queued" or "preset-interrupted")
                window.applicationPresetPending = new(window.applicationPresetReview!, screen == "preset-interrupted");
            window.Render(); return window;
        }
        if (screen == "server-update")
        {
            var server = window.catalog!.Servers.First();
            window.selectedServer = server.Id; window.updateViewServer = server.Id;
            window.updateCheckResult = new(server.Id, 1, 2, new(false, true, false, "Update saved for the next stop."));
            window.updateCheckNotice = "Update saved for the next stop.";
            window.Render(); return window;
        }
        if (screen == "asa-mod-details")
        {
            var server = window.catalog!.Servers.First();
            window.selectedServer = server.Id; window.managementTab = 2; window.modPageServer = server.Id;
            window.catalog.Servers[window.catalog.Servers.IndexOf(server)] = server with { Edition = ArkEdition.ASA };
            window.modBrowser = true; window.lookupId = "1087407";
            window.lookedUpMod = new("1087407", ArkEdition.ASA, "World Builder (Freemium)", "A mod to modify the world for any ARK Map.",
                null, new Uri("https://www.curseforge.com/ark-survival-ascended/mods/world-builder"), DateTimeOffset.Parse("2026-07-09T22:48:50Z"), ["Rebalancing", "Game Modes"]);
            window.Render(); return window;
        }
        if (screen == "mod-order-save")
        {
            var server = window.catalog!.Servers.First();
            window.selectedServer = server.Id; window.managementTab = 2; window.modPageServer = server.Id;
            window.modOrderDrafts[server.Id] = ["731604991", "924619115"];
            window.modOrderConfirmation = true; window.reviewLiveModOrder = true;
            window.Render(); return window;
        }
        if (screen == "preset-edit")
        {
            window.settingsPage = true; window.applicationCategory = 5;
            var server = window.catalog!.Servers.First();
            window.BeginPresetEdit(new(Guid.NewGuid(), new("Solo settings", server.Edition, server.Settings.Copy(), ["731604991"])));
            return window;
        }
        if (screen == "preset-create")
        {
            window.settingsPage = true; window.applicationCategory = 5; window.presetCreating = true;
            window.Render(); return window;
        }
        if (screen == "app-storage")
        {
            window.settingsPage = true; window.applicationCategory = 4;
            window.Render(); return window;
        }
        if (screen == "cluster-restore-recovery")
        {
            var cluster = window.catalog!.Clusters.First();
            var maps = window.catalog.Servers.Where(s => s.ClusterId == cluster.Id).Select(s => s.Id).ToList();
            window.selectedCluster = cluster.Id; window.managementTab = 3; window.clusterBackupScope = cluster.Id;
            window.clusterRecoveryCluster = cluster.Id;
            window.clusterRecoveryRecords = [new(1, Guid.NewGuid(), cluster.Id, cluster.Edition, 8,
                new(Guid.NewGuid(), DateTimeOffset.Now.AddHours(-1)), maps, ClusterRestorePhase.Replacing)];
            window.clusterRecoveryNotice = "Design review · Illustrative operation; no files read.";
            window.Render(); return window;
        }
        if (screen == "shared-restore-recovery")
        {
            var cluster = window.catalog!.Clusters.First();
            window.selectedCluster = cluster.Id; window.managementTab = 3; window.clusterBackupScope = cluster.Id;
            window.sharedRecoveryCluster = cluster.Id; window.clusterSharedBackups = true;
            window.sharedRecoveryRecords = [new(Guid.NewGuid(), cluster.Id, cluster.Edition, 8, true, WorldRestorePhase.Replacing),
                new(Guid.NewGuid(), cluster.Id, cluster.Edition, 7, true, WorldRestorePhase.Cleaned)];
            window.sharedRecoveryNotice = "Design review · Illustrative recovery; no files read.";
            window.Render(); return window;
        }
        if (screen == "restore-recovery")
        {
            var server = window.catalog!.Servers.First(); var operation = Guid.NewGuid();
            window.selectedServer = server.Id; window.managementTab = 3; window.recoveryViewServer = server.Id;
            window.recoveryRecords = [new(operation, server.Id, server.Edition, 8, server.Directory, server.Directory, server.Directory,
                WorldRestorePhase.Replacing) { OriginalWorldFolders = ["SavedArks", "SaveGames"] }];
            window.recoveryNotice = "Design review · Illustrative interrupted operation; no files read.";
            window.Render(); return window;
        }
        if (screen is "coordinated-history" or "coordinated-point" or "coordinated-unavailable")
        {
            var cluster = window.catalog!.Clusters.First();
            var maps = window.catalog.Servers.Where(s => s.ClusterId == cluster.Id).Select(s => s.Id).ToList();
            window.selectedCluster = cluster.Id; window.managementTab = 3; window.clusterBackupScope = cluster.Id;
            window.reviewCoordinatedHistory = true;
            var point = new CoordinatedRestorePoint(1, cluster.Id, cluster.Edition, 8,
                new(Guid.NewGuid(), DateTimeOffset.Now.AddHours(-1)), maps);
            window.coordinatedHistory = new(cluster.Id, cluster.Edition, TimeSpan.FromHours(8).Ticks, [point], null);
            if (screen == "coordinated-point")
            {
                window.coordinatedPoint = point; window.coordinatedTargetChosen = true; window.coordinatedTarget = maps.First();
                window.coordinatedChecks[(point.Capture.Id, maps.First())] = new CoordinatedCheck
                {
                    Result = new(cluster.Id, maps.First(), point.RuntimeHour, point.Capture, 12, 1024000, DateTimeOffset.Now),
                    Notice = "Design review · Illustrative verification; no archive read."
                };
            }
            window.coordinatedNotice = "Design review · Illustrative point; no archives read.";
            if (screen == "coordinated-unavailable")
            {
                window.coordinatedHistory = null;
                window.coordinatedNotice = "Could not refresh coordinated restore points. Try again when connected.";
            }
            window.Render(); return window;
        }
        if (screen == "shared-cluster-history")
        {
            var cluster = window.catalog!.Clusters.First();
            window.selectedCluster = cluster.Id; window.managementTab = 3; window.clusterBackupScope = cluster.Id;
            window.clusterSharedBackups = true; window.reviewSharedClusterHistory = true;
            window.sharedClusterHistory = new(1, cluster.Id, cluster.Edition, TimeSpan.FromHours(8).Ticks, null, null,
                [new(8, "hour-00000008.zip", DateTimeOffset.Now.AddHours(-1)),
                    new(7, "hour-00000007.zip", DateTimeOffset.Now.AddHours(-2))], null)
                { Scope = SnapshotScope.SharedCluster };
            window.sharedHistoryNotice = "Design review · Illustrative history; no files read.";
            window.sharedArchiveChecks[(cluster.Id, 8)] = new SharedArchiveCheck
            {
                Result = new(cluster.Id, 8, 2, 1024, DateTimeOffset.Now, null),
                Notice = "Design review · Illustrative verification; no archive read."
            };
            window.Render(); return window;
        }
        if (screen == "world-restore")
        {
            var server = window.catalog!.Servers.First();
            window.selectedServer = server.Id; window.managementTab = 3; window.backupViewServer = server.Id;
            window.selectedRestorePoint = new(8, "hour-00000008.zip", DateTimeOffset.Now.AddHours(-1));
            window.backupChecks[(server.Id, 8)] = new BackupCheckViewState
            {
                Result = new(server.Id, 8, 12, 1024000, DateTimeOffset.Now) { Layout = SnapshotLayout.WorldSavedFolders },
                Notice = "Design review · Illustrative archive; no world restored."
            };
            window.Render(); return window;
        }
        if (screen is "basic-settings" or "network-settings" or "queued-settings")
        {
            var server = window.catalog!.Servers.First();
            window.selectedServer = server.Id; window.managementTab = 1;
            window.settingsCategory = screen == "network-settings" ? 1 : 0;
            var values = BasicSettingsValues.From(server.Settings);
            window.basicEditors[server.Id] = new BasicEditor
            {
                Review = new(server.Id, server.Directory, server.Edition, server.Map, server.ClusterId, values,
                    QueuedSettings: screen == "queued-settings" ? values with { MaxPlayers = 80 } : null,
                    QueuedConfigurationChanged: screen == "queued-settings"),
                Draft = values, Notice = "Design review · Illustrative settings; no server changed."
            };
            window.Render(); return window;
        }
        if (screen == "command-console")
        {
            var server = window.catalog!.Servers.First();
            window.selectedServer = server.Id; window.managementTab = 5;
            window.commandConsoles[server.Id] = new CommandConsoleState
            {
                Responses = [new(server.Id, DateTimeOffset.Now, "Illustrative server response\nNo Players Connected")],
                Notice = "Design review · Illustrative response; no command sent."
            };
            window.Render(); return window;
        }
        if (screen == "reported-players")
        {
            var server = window.catalog!.Servers.First();
            window.selectedServer = server.Id; window.managementTab = 4;
            window.playerObservations[server.Id] = new PlayerObservationView
            {
                Result = new(server.Id, DateTimeOffset.Now, [new("River", "illustrative-account-42"), new("Survivor", "illustrative-account-77")]),
                Notice = "Design review · Illustrative player response; no server queried."
            };
            window.Render(); return window;
        }
        if (screen == "advanced-editor")
        {
            var server = window.catalog!.Servers.First();
            window.selectedServer = server.Id; window.managementTab = 1; window.advancedEditorServer = server.Id;
            window.advancedEditorFile = 0;
            window.advancedPreviewDrafts[(server.Id, 0)] = "; Configuration example\n[SessionSettings]\nSessionName=Island Test\n\n[ServerSettings]\nMaxPlayers=70\nServerPVE=True\n\n[MessageOfTheDay]\nMessage=Welcome to our ARK!\nDuration=45\n";
            window.Render(); return window;
        }
        if (screen == "server-welcome")
        {
            var server = window.catalog!.Servers.First();
            window.selectedServer = server.Id; window.managementTab = 1; window.settingsCategory = 0;
            window.welcomeEditors[server.Id] = new WelcomeEditor { Hash = new string('0', 64), Message = "Welcome to our ARK!",
                Duration = 45, Expanded = true, Notice = "Changes apply to this map. Stop it before saving." };
            window.Render(); return window;
        }
        window.ResetCreationDraft(); window.creationPage = true;
        if (screen == "creation-type") { window.Render(); return window; }
        window.draftCluster = true; window.draftEdition = ArkEdition.ASE; window.draftMap = "TheIsland";
        window.draftName = "Main PvE Island"; window.draftClusterName = "Main PvE Cluster";
        window.draftCapacity = 70; window.draftMode = "PvE"; window.draftInternet = false;
        window.draftAdminPassword = "design-fixture-not-a-real-password";
        window.draftDescription = "Welcome to our ARK!";
        if (screen == "creation-preset")
        {
            window.creationStep = 3; window.customizationCategory = 1;
            window.displayedPresets = [new(Guid.NewGuid(), new("Solo settings", ArkEdition.ASE,
                new() { Name = "Solo ARK", MaxPlayers = 12, GamePort = 7777, QueryPort = 27015, RconPort = 32330 }, []))];
        }
        else if (screen == "creation-access") { window.creationStep = 3; window.customizationCategory = 2; }
        else if (screen is "creation-mods" or "creation-mods-invalid" or "creation-mods-review")
        {
            window.creationStep = 3; window.customizationCategory = 4;
            window.creationModDraft = "2182894352";
        }
        else if (screen == "creation-review") window.creationStep = 4;
        else if (screen == "cluster-created")
        {
            window.creationStep = 5;
            window.createdArks.Add(new(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.GetTempPath(),
                new() { Name = window.draftName, MaxPlayers = 70, GamePort = 7777, QueryPort = 27015, RconPort = 32330 }));
        }
        else throw new ArgumentException("Choose a supported design review screen.");
        window.Render(); return window;
    }
}
