using ArkServerManager.Core;
using System.Text.Json;

if (args is not ["--data-root", var root])
{
    Console.Error.WriteLine("The background host requires --data-root <dedicated folder>.");
    return 2;
}
root = Path.GetFullPath(root);
using var applicationLease = AppFileLease.OpenUsageIfInstalled(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")));
using var shutdown = new CancellationTokenSource();
WindowsSessionShutdown? windowsSession = null;
using var terminateSignal = OperatingSystem.IsLinux() ? System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; shutdown.Cancel(); }) : null;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
var existingImportReviews = new ExistingServerImportReviewService(repository);
var existingRegistration = new ExistingServerRegistration(repository, existingImportReviews, async (directory, ct) =>
{
    if ((await new InstallationStopGuard().CheckAsync(directory, ct)).State != InstallationStopState.Stopped)
        throw new InvalidOperationException("Stop this installation and verify its state before importing it.");
});
var serverStartup = new ServerStartupPreferenceStore(root, repository);
var presetLibrary = new PresetLibrary(Path.Combine(root, "app-settings", "presets.json"));
var presets = new PresetManagerOperations(presetLibrary);
var presetApplicationReview = new PresetApplicationReviewService(repository, presetLibrary);
var activity = new ActivityJournal(Path.Combine(root, "logs", "activity.jsonl"));
await using var operations = new BackgroundOperations(capacity: 128, parallelism: 2,
    recordCompletion: operation => BackgroundActivityRecorder.RecordAsync(activity, operation));
// All future production lifecycle/file adapters must use this same host gate.
using var fileOperationGate = new SemaphoreSlim(1, 1);
using var installationHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
await using var snapshotBroker = OperatingSystem.IsWindows() ? new WindowsSnapshotBroker(Path.Combine(AppContext.BaseDirectory, "WindowsSnapshot", "ArkServerManager.WindowsSnapshot.exe")) : null;
await using var linuxSnapshots = OperatingSystem.IsLinux() ? new LinuxBackupCaptureProvider(Path.Combine(root, "Dependencies")) : null;
string? linuxSnapshotCleanupProblem = null;
string? nativeDriveCleanupProblem = null;
var restoreCleanupProblems = new Dictionary<Guid, string>();
var nativePlatform = OperatingSystem.IsWindows() ? ServerPlatform.Windows : ServerPlatform.Linux;
var nativeLaunchDrives = OperatingSystem.IsWindows() ? new WindowsLaunchDrives(
    Path.Combine(root, "app-settings", "native-launch-drives.json"), new NativeWindowsLaunchDriveBackend(),
    async (directory, ct) =>
    {
        var scan = await new ServerProcessDiscovery().ScanAsync(ct);
        return scan.NativeScanComplete && !scan.Candidates.Any(c => WithinDirectory(directory, c.Directory));
    }) : null;
bool WithinDirectory(string parent, string child)
{
    var relative = Path.GetRelativePath(parent, child);
    return relative == "." || !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar);
}
var steamCmdFolder = Path.Combine(root, "Dependencies", "SteamCMD", nativePlatform.ToString());
var steamCmdExecutable = Path.Combine(steamCmdFolder, nativePlatform == ServerPlatform.Windows ? "steamcmd.exe" : "steamcmd.sh");
var installationResumeGuard = new ServerInstallationResumeGuard(steamCmdExecutable);
var creationOperations = new ServerCreationManagerOperations(repository, root, operations, fileOperationGate,
    async (record, change, ct) =>
    {
        if (!await installationResumeGuard.VerifyAsync(record, ct))
            throw new InvalidOperationException("The setup's server and installer could not be verified as stopped. Creation has not continued.");
        await change(ct);
    });
async Task EnsureSteamCmdRuntimeAsync(CancellationToken ct)
{
    if (OperatingSystem.IsLinux())
    {
        var result = await new LinuxRequiredToolSetup(root, steamCmd: true).EnsureAsync(_ => { }, ct);
        if (!result.Ready) throw new IOException(result.Notice);
    }
}
async Task<string> EnsureSteamCmdAsync(CancellationToken ct)
{
    await EnsureSteamCmdRuntimeAsync(ct);
    return await new SteamCmdBootstrap(installationHttp).EnsureAsync(steamCmdFolder, nativePlatform, ct);
}
var installationOperations = new ServerInstallationManagerOperations(root, operations, fileOperationGate, async ct =>
{
    for (var path = steamCmdExecutable; path is not null; path = Path.GetDirectoryName(path))
        if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The required-tool location uses a linked path. Review installation settings.");
    var executable = await EnsureSteamCmdAsync(ct);
    return new ServerInstallationPreparation(repository, new SteamCmdInstaller(executable, Path.Combine(root, "logs", "installation")));
}, async (record, change, ct) =>
{
    if (!await installationResumeGuard.VerifyAsync(record, ct))
        throw new InvalidOperationException("The staged server or installer could not be verified as stopped. No download was resumed.");
    await change(ct);
});
var recoveryStopGuard = new DeletionRecoveryStopGuard();
var clusterRunningTime = new ClusterRunningTimeTracker();
SettingsChangeWorkflow pendingSettings = null!;
ModOrderWorkflow pendingModOrder = null!;
ServerUpdateWorkflow pendingServerUpdates = null!;
async Task RequireAccessStoppedAsync(ServerDefinition server, CancellationToken ct)
{
    if ((await new InstallationStopGuard().CheckAsync(server.Directory, ct)).State != InstallationStopState.Stopped)
        throw new InvalidOperationException("The selected installation could not be verified stopped before changing access settings.");
}
var pendingAccess = new ServerAccessChanges(repository, Path.Combine(root, "app-settings", "pending-access.json"), RequireAccessStoppedAsync);
var pendingConfiguration = new PendingConfigurationEdits(repository, Path.Combine(root, "app-settings", "pending-configuration.json"), RequireAccessStoppedAsync);
var pendingPresets = new PresetApplicationQueue(repository, Path.Combine(root, "app-settings", "pending-presets.json"),
    async (server, ct) =>
    {
        if ((await new InstallationStopGuard().CheckAsync(server.Directory, ct)).State != InstallationStopState.Stopped)
            throw new InvalidOperationException("The selected installation could not be verified as stopped before applying its preset.");
    }, DeleteRemovedModFilesAsync, supportsAsaRemoval: OperatingSystem.IsWindows() || OperatingSystem.IsLinux());
var joiningJournal = new ClusterJoiningJournal(root);
var joiningTransaction = new ClusterJoiningTransaction(repository, joiningJournal, presetApplicationReview, pendingPresets,
    RequireAccessStoppedAsync, async (id, ct) =>
    {
        await RequireNoPresetConflictsAsync(id, ct);
        if ((await pendingAccess.ReadAsync(id, ct)).PendingId is not null)
            throw new InvalidOperationException("Apply saved access settings before adding this server to a cluster.");
        await WorldRestoreRecovery.RequireConsistentAsync((await repository.ReadAsync(ct)).Servers.Single(s => s.Id == id), ct);
    }, requireTargetReady: async (id, ct) =>
    {
        var current = await repository.ReadAsync(ct);
        if (current.Clusters.Any(c => c.Id == id))
        {
            await ClusterRestoreRecords.RequireConsistentAsync(current, id, root, ct);
            await SharedTransferRestore.RequireConsistentAsync(current, id, root, ct);
        }
    });
Task DeleteRemovedModFilesAsync(ServerDefinition server, IReadOnlyList<string> removed, CancellationToken ct)
    => server.Edition == ArkEdition.ASE ? RemovedAseModFiles.DeleteAsync(server, removed, ct)
        : OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ? RemovedAsaModFiles.DeleteAsync(server, removed, ct)
        : throw new NotSupportedException("ASA cache cleanup requires its verified platform adapter.");
async Task ApplyPendingPresetAsync(Guid id, CancellationToken ct)
{
    if (await pendingPresets.ReadAsync(id, ct) is null) return;
    await RequireNoPresetConflictsAsync(id, ct);
    await pendingPresets.ApplyBeforeStartAsync(id, ct);
}
async Task RequireNoPresetConflictsAsync(Guid id, CancellationToken ct)
{
    if (await pendingSettings.ReadPendingAsync(id, ct) is not null || (await pendingModOrder.ReadAsync(id, ct)).Order is not null
        || (await pendingConfiguration.ReadAsync(id, ct)).Count > 0)
        throw new InvalidOperationException("Apply the selected server's saved settings and mod changes before saving or applying a preset.");
}
async Task RequireNoPendingPresetAsync(Guid id, CancellationToken ct)
{
    await RequireNoJoiningAsync(id, ct);
    await RequireNoPendingConfigurationAsync(id, ct);
    if (await pendingPresets.ReadAsync(id, ct) is not null)
        throw new InvalidOperationException("The selected server has a saved preset. Apply or review it before making another configuration change.");
}
async Task RequireNoJoiningAsync(Guid id, CancellationToken ct)
{
    if ((await joiningJournal.ReadAsync(ct)).Any(r => r.Originals.Any(s => s.Id == id)))
        throw new InvalidOperationException("Finish the recorded cluster joining before changing this server's settings or canceling its inherited application.");
}
async Task RequireNoJoiningClusterAsync(Guid id, CancellationToken ct)
{
    if ((await joiningJournal.ReadAsync(ct)).Any(r => r.Target.Id == id))
        throw new InvalidOperationException("Finish the recorded joining request before changing this cluster's defaults.");
}
async Task RequireNoPendingConfigurationAsync(Guid id, CancellationToken ct)
{
    if ((await pendingConfiguration.ReadAsync(id, ct)).Count > 0)
        throw new InvalidOperationException("This map has saved advanced configuration. Apply or review it before making another configuration change.");
}
async Task RequireNoAdvancedConflictsAsync(Guid id, CancellationToken ct)
{
    await RequireNoJoiningAsync(id, ct);
    if (await pendingPresets.ReadAsync(id, ct) is not null || await pendingSettings.ReadPendingAsync(id, ct) is not null
        || (await pendingModOrder.ReadAsync(id, ct)).Order is not null || (await pendingAccess.ReadAsync(id, ct)).PendingId is not null)
        throw new InvalidOperationException("Apply or review this map's saved settings, passwords, mods or preset before saving advanced configuration.");
}
var windowsRuntimePreparation = new ServerRuntimePreparation(WindowsServerRuntimeLibraries.Check, async (server, ct) =>
{
    var tool = await EnsureSteamCmdAsync(ct);
    var repaired = await new SteamCmdInstaller(tool, Path.Combine(root, "logs", "runtime-repair"))
        .InstallOrUpdateAsync(new(server.Edition, nativePlatform, server.Directory, true), cancellationToken: ct);
    return repaired.Success;
});
var runtimeFactory = new RegisteredRconRuntimeFactory(repository,
    async (server, ct) =>
    {
        // Initial launches and automatic recovery both own the lifecycle/file scope here.
        await WorldRestoreRecovery.RequireConsistentAsync(server, ct);
        var catalog = await repository.ReadAsync(ct);
        var current = catalog.Servers.Single(s => s.Id == server.Id);
        if (current.Directory != server.Directory || current.Edition != server.Edition || current.ClusterId != server.ClusterId)
            throw new InvalidOperationException("The selected installation changed before startup.");
        var check = await new InstallationStopGuard().CheckAsync(current.Directory, ct);
        if (check.State != InstallationStopState.Stopped)
            throw new InvalidOperationException("The selected installation could not be verified as stopped. No second server was started.");
        if (current.ClusterId is Guid clusterId)
        {
            await ClusterRestoreRecords.RequireConsistentAsync(catalog, clusterId, root, ct);
            await SharedTransferRestore.RequireConsistentAsync(catalog, clusterId, root, ct);
            ManagedClusterStorage.Prepare(catalog, clusterId, root);
        }
        var queuedUpdate = await pendingServerUpdates.OnStoppedAsync(server.Id, ct);
        if (queuedUpdate.Queued) throw new IOException(queuedUpdate.Message);
        await SharedServerFiles.RefreshBeforeStartAsync(root, current, nativePlatform, ct);
        await pendingConfiguration.ApplyBeforeStartAsync(server.Id, ct);
        await ApplyPendingPresetAsync(server.Id, ct);
        await pendingSettings.ApplyBeforeStartAsync(server.Id, ct);
        await pendingModOrder.ApplyBeforeStartAsync(server.Id, ct);
        await pendingAccess.ApplyBeforeStartAsync(server.Id, ct);
        catalog = await repository.ReadAsync(ct);
        current = catalog.Servers.Single(s => s.Id == server.Id);
        await EnsureSteamCmdRuntimeAsync(ct);
        await new AseAutomaticMods(installationHttp).PrepareAsync(current, nativePlatform, ct);
        if (OperatingSystem.IsWindows()) await windowsRuntimePreparation.PrepareAsync(current, ct);
        if (OperatingSystem.IsLinux() && current.Edition == ArkEdition.ASA)
        {
            var tools = await new LinuxAsaCompatibilitySetup(root, installationHttp).PrepareAsync(current, _ => { }, ct);
            return LinuxAsaLaunchPlan.BuildManaged(catalog, current.Id, root, tools.Python, tools.Launcher, false,
                Path.Combine(current.Directory, "logs", "console.log"));
        }
        var launch = NativeServerLaunchPlan.BuildManaged(catalog, current.Id, root, nativePlatform, false,
            Path.Combine(current.Directory, "logs", "console.log"));
        if (nativeLaunchDrives is not null && current.Mods.Count > 0)
        {
            // Maps inside the chosen management folder share one native drive.
            var mappingRoot = WithinDirectory(root, current.Directory) ? root : current.Directory;
            var alias = await nativeLaunchDrives.GetOrCreateAsync(mappingRoot, ct);
            var aliasServer = Path.Combine(alias, Path.GetRelativePath(mappingRoot, current.Directory));
            launch = NativeServerLaunchPlan.WithWindowsLaunchRoot(launch, current.Directory, aliasServer);
        }
        return launch;
    },
    (process, endpoint, ct) => new ArkGracefulShutdown().RequestWithResponseConfirmationAsync(process, endpoint,
        response => response.Trim().Equals("World Saved", StringComparison.OrdinalIgnoreCase), ct),
    (id, incident) => activity.RecordIncidentAsync(id, incident),
    TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2),
    RconResponseMode.SinglePacket, clusterRunningTime.Observe, fileOperationGate, AseInstalledModFiles.RequireAsync);
var lifecycle = new ServerLifecycleCoordinator(repository, runtimeFactory.Create,
    async (id, ct) =>
    {
        await joiningTransaction.ResumeForServerAsync(id, ct);
        var document = await repository.ReadAsync(ct);
        var server = document.Servers.Single(s => s.Id == id);
        await WorldRestoreRecovery.RequireConsistentAsync(server, ct);
        if (server.ClusterId is Guid clusterId)
        {
            await ClusterRestoreRecords.RequireConsistentAsync(document, clusterId, root, ct);
            await SharedTransferRestore.RequireConsistentAsync(document, clusterId, root, ct);
        }
        await ApplyPendingPresetAsync(id, ct);
        await pendingSettings.ApplyBeforeStartAsync(id, ct);
    }, fileOperationGate,
    (server, ct) => new InstallationStopGuard().CheckAsync(server.Directory, ct),
    async (server, ct) =>
    {
        try
        {
            await WorldRestoreRecovery.CleanupAfterVerifiedStartAsync(server, ct);
            if (server.ClusterId is Guid clusterId)
            {
                var current = await repository.ReadAsync(ct);
                await SharedTransferRestore.CleanupAfterClusterStartAsync(current, clusterId, root,
                    clusterRunningTime.AllRunning, ct);
                await ClusterRestoreCleanup.AfterVerifiedStartAsync(current, clusterId, root, clusterRunningTime.AllRunning, ct);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            await activity.AppendAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "restore", ActivitySeverity.Warning,
                "The map started, but cleanup of previous restored data needs review.", server.Id, Stage: "restore-cleanup"));
        }
    }, async (server, ct) =>
    {
        var update = await pendingServerUpdates.OnStoppedAsync(server.Id, ct);
        if (update.Queued) throw new IOException(update.Message);
        await CleanupNativeLaunchPathsAsync(ct);
    });
pendingSettings = new SettingsChangeWorkflow(repository, Path.Combine(root, "app-settings", "pending-settings.json"),
    async (ids, ct) =>
    {
        var stopped = true;
        foreach (var id in ids) stopped &= (await lifecycle.ExecuteMemberAsync(id, LifecycleAction.Stop, ct)).Success;
        return stopped;
    },
    async (ids, ct) =>
    {
        foreach (var id in ids)
            if (!(await lifecycle.ExecuteMemberAsync(id, LifecycleAction.Start, ct)).Success)
                throw new InvalidOperationException("A server could not restart after its settings were applied.");
    },
    (id, settings, ct) => WriteBasicConfigurationAsync(id, settings, null, ct),
    async (id, change, ct) =>
    {
        var selected = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == id);
        await lifecycle.WithStoppedMemberAsync(selected, async token =>
            { await RequireNoPendingPresetAsync(id, token); await change(token); },
            (server, token) => new InstallationStopGuard().CheckAsync(server.Directory, token), ct);
    }, WriteBasicConfigurationAsync, WithRunningConfigurationQueueAsync);

async Task WithRunningConfigurationQueueAsync(Guid id, Func<CancellationToken, Task> change, CancellationToken ct)
{
    await lifecycle.WithStatusScopeAsync(async (statuses, token) =>
    {
        var status = statuses.Single(s => s.ServerId == id);
        if (!status.Managed || status.State != ServerState.Running)
            throw new InvalidOperationException("The server state changed. Review it before saving changes for the next start.");
        await RequireNoPendingPresetAsync(id, token);
        await change(token);
    }, ct);
}

async Task WriteBasicConfigurationAsync(Guid id, ServerSettings settings, string? reviewedHash, CancellationToken ct)
    {
        var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == id);
        if (settings.Values.Count != server.Settings.Values.Count
            || settings.Values.Any(pair => !server.Settings.Values.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new NotSupportedException("These pending settings need their gameplay configuration adapter before startup.");
        var source = await ServerConfigurationReader.ReadAsync(repository, new(id, "GameUserSettings.ini"), ct);
        if (reviewedHash is not null && !string.Equals(source.SourceSHA256, reviewedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The configuration changed after review. Pending settings were retained; reload before applying them.");
        var text = BasicServerConfiguration.Apply(source.Text ?? throw new InvalidDataException("The server configuration is missing."), settings);
        await ServerConfigurationWriter.WriteAsync(repository, new(id, "GameUserSettings.ini", source.SourceSHA256!, text),
            async (selected, change, token) =>
            {
                // Both beforeStart and the immediate-save scope already hold the coordinator's gate.
                if ((await new InstallationStopGuard().CheckAsync(selected.Directory, token)).State != InstallationStopState.Stopped)
                    throw new InvalidOperationException("Pending settings require a verified stopped installation.");
                await change(token);
            }, ct);
    }
async Task<ServerPreset> CapturePresetAsync(ManagerRequest request, CancellationToken ct)
{
    var selection = request.Payload?.Deserialize<PresetCaptureRequest>() ?? throw new ArgumentException("Choose a server to capture.");
    await fileOperationGate.WaitAsync(ct);
    try { return await PresetCapture.ReadAsync(repository, selection, ct); }
    finally { fileOperationGate.Release(); }
}
var lifecycleOperations = new LifecycleManagerOperations(lifecycle, operations,
    ct => lifecycle.WithStatusScopeAsync((_, token) => CleanupNativeLaunchPathsAsync(token), ct));
var accessOperations = new ServerAccessManagerOperations(repository, pendingAccess, operations, lifecycle.WithStatusScopeAsync,
    RequireAccessStoppedAsync, lifecycle.ExecuteMemberAsync, RequireNoPendingConfigurationAsync);
var restartSchedules = new DailyRestartSchedules(repository, Path.Combine(root, "app-settings", "restart-schedules.json"));
var restartScheduleOperations = new DailyRestartManagerOperations(restartSchedules, operations);
var clusterDefaultsOperations = new ClusterDefaultsManagerOperations(repository, operations,
    (work, ct) => lifecycle.WithStatusScopeAsync((_, token) => work(token), ct), RequireNoJoiningClusterAsync);
var clusterMembershipOperations = new ClusterMembershipManagerOperations(repository, operations, lifecycle, new InstallationStopGuard(), joiningTransaction);
var firewallOperations = new WindowsFirewallManagerOperations(repository, operations, lifecycle, managementRoot: root);
var aseClientOperations = new AseClientManagerOperations(repository, operations, lifecycle);
var asaClientOperations = new AsaClientManagerOperations(repository, operations, lifecycle);
var routerOperations = new RouterManagerOperations(root, repository, operations, lifecycle);
var repairOperations = new ServerRepairManagerOperations(repository, operations, lifecycle,
    (server, ct) => new InstallationStopGuard().CheckAsync(server.Directory, ct), async (server, progress, ct) =>
    {
        var tool = await EnsureSteamCmdAsync(ct);
        var result = await new SteamCmdInstaller(tool, Path.Combine(root, "logs", "repairs"))
            .InstallOrUpdateAsync(new(server.Edition, nativePlatform, server.Directory, true),
                new Progress<IReadOnlyList<ProcessLine>>(lines =>
                { if (lines.LastOrDefault() is { } line) progress(new(line.Text)); }), ct);
        return result.Success;
    }, managementRoot: root);
var basicSettingsOperations = new BasicSettingsManagerOperations(repository, operations, pendingSettings, async (server, ct) =>
{
    var status = (await lifecycle.StatusAsync(ct)).Single(s => s.ServerId == server.Id);
    if (status.State is ServerState.Starting or ServerState.Stopping or ServerState.Recovering)
        throw new InvalidOperationException("Wait for the current server action before saving settings.");
    if (status.Managed && status.State == ServerState.Running) return true;
    var check = await new InstallationStopGuard().CheckAsync(server.Directory, ct);
    if (check.State == InstallationStopState.Stopped) return false;
    throw new InvalidOperationException("The server is running outside this manager or its state is unavailable. Review it before saving settings.");
});
Func<ServerDefinition, Func<CancellationToken, Task>, CancellationToken, Task> stoppedConfigurationScope = async (server, change, ct) =>
{
    await lifecycle.WithStoppedMemberAsync(server, async token =>
        { await RequireNoPendingPresetAsync(server.Id, token); await change(token); },
        (selected, token) => new InstallationStopGuard().CheckAsync(selected.Directory, token), ct);
};
var welcomeOperations = new ServerWelcomeManagerOperations(repository, operations, stoppedConfigurationScope);
pendingModOrder = new ModOrderWorkflow(repository, Path.Combine(root, "app-settings", "pending-mod-order.json"),
    async (ids, ct) => (await lifecycle.ExecuteMemberAsync(ids.Single(), LifecycleAction.Stop, ct)).Success,
    async (ids, ct) =>
    {
        if (!(await lifecycle.ExecuteMemberAsync(ids.Single(), LifecycleAction.Start, ct)).Success)
            throw new InvalidOperationException("The selected map could not restart after saving its mod order.");
    }, WriteModOrderAsync,
    async (id, change, ct) => await stoppedConfigurationScope((await repository.ReadAsync(ct)).Servers.Single(s => s.Id == id), change, ct),
    DeleteRemovedModFilesAsync, WithRunningConfigurationQueueAsync, supportsAsaRemoval: OperatingSystem.IsWindows() || OperatingSystem.IsLinux());
var modOrderOperations = new ModOrderManagerOperations(repository, operations, pendingModOrder, async (server, ct) =>
{
    var status = (await lifecycle.StatusAsync(ct)).Single(s => s.ServerId == server.Id);
    if (status.Managed && status.State == ServerState.Running) return true;
    if (status.State is ServerState.Starting or ServerState.Stopping or ServerState.Recovering)
        throw new InvalidOperationException("Wait for the current server action before saving mod order.");
    if ((await new InstallationStopGuard().CheckAsync(server.Directory, ct)).State == InstallationStopState.Stopped) return false;
    throw new InvalidOperationException("The selected installation could not be verified as stopped or managed.");
}, (id, ct) => new SteamWorkshopMetadata(installationHttp).GetAsync(id, ct));
var presetApplicationOperations = new PresetApplicationManagerOperations(presetApplicationReview, pendingPresets, operations,
    lifecycle.WithStatusScopeAsync, async (id, ct) => { await RequireNoJoiningAsync(id, ct); await RequireNoPresetConflictsAsync(id, ct); }, lifecycle.ExecuteMemberAsync,
    RequireNoJoiningAsync);
var clusterPresetOperations = new ClusterPresetManagerOperations(repository, presetApplicationReview, presetApplicationOperations, operations,
    (work, ct) => lifecycle.WithStatusScopeAsync((_, token) => work(token), ct), RequireNoJoiningClusterAsync);
var clusterOverrideOperations = new ClusterOverrideManagerOperations(repository, operations, lifecycle, presetApplicationReview, presetApplicationOperations, pendingPresets,
    RequireNoJoiningAsync);
async Task WriteModOrderAsync(Guid id, IReadOnlyList<string> order, CancellationToken ct)
{
    AsaModLaunchArguments.Build(order);
    var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == id);
    if (server.Edition == ArkEdition.ASA) return; // Fresh launch arguments carry ASA order.
    var user = await ServerConfigurationReader.ReadAsync(repository, new(id, "GameUserSettings.ini"), ct);
    var game = await ServerConfigurationReader.ReadAsync(repository, new(id, "Game.ini"), ct);
    if (user.Text is null || user.SourceSHA256 is null)
        throw new InvalidDataException("Prepare GameUserSettings.ini before saving mod order.");
    var userIni = new IniDocument(user.Text); var gameIni = new IniDocument(game.Text ?? "");
    userIni.SetValues("ServerSettings", "ActiveMods", order.Count > 0 ? [string.Join(",", order)] : []);
    gameIni.SetValues("ModInstaller", "ModIDS", order);
    async Task VerifiedStopped(ServerDefinition selected, Func<CancellationToken, Task> change, CancellationToken token)
        {
            if ((await new InstallationStopGuard().CheckAsync(selected.Directory, token)).State != InstallationStopState.Stopped)
                throw new InvalidOperationException("Mod order requires a verified stopped installation.");
            await change(token);
        }
    if (game.Text is null)
        await ServerConfigurationWriter.CreateGameAsync(repository, id, gameIni.ToString(), VerifiedStopped, ct);
    else
        await ServerConfigurationWriter.WriteAsync(repository, new(id, "Game.ini", game.SourceSHA256!, gameIni.ToString()), VerifiedStopped, ct);
    await ServerConfigurationWriter.WriteAsync(repository, new(id, "GameUserSettings.ini", user.SourceSHA256, userIni.ToString()), VerifiedStopped, ct);
}
var configurationOperations = new ServerConfigurationManagerOperations(repository, operations, stoppedConfigurationScope,
    pendingConfiguration, lifecycle.WithStatusScopeAsync, RequireNoAdvancedConflictsAsync, lifecycle.ExecuteMemberAsync);
pendingServerUpdates = new ServerUpdateWorkflow(Path.Combine(root, "app-settings", "pending-server-updates.json"),
    (_, _) => Task.FromResult(RunningUpdateChoice.UpdateWhenStopped),
    (_, _) => throw new InvalidOperationException("Use the selected map's restart action to apply an update now."),
    async (id, build, ct) =>
    {
        var selected = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == id);
        if ((await new InstallationStopGuard().CheckAsync(selected.Directory, ct)).State != InstallationStopState.Stopped)
            throw new InvalidOperationException("Server updates require a verified stopped installation.");
        await SharedServerFiles.RefreshBeforeStartAsync(root, selected, nativePlatform, ct);
        if (await ServerUpdateManagerOperations.InstalledBuildAsync(selected, ct) == build) return true;
        var tool = await EnsureSteamCmdAsync(ct);
        var installed = await new SteamCmdInstaller(tool, Path.Combine(root, "logs", "updates"))
            .InstallOrUpdateAsync(new(selected.Edition, nativePlatform, selected.Directory, false), cancellationToken: ct);
        return installed.Success && await ServerUpdateManagerOperations.InstalledBuildAsync(selected, ct) == build;
    }, (_, _) => throw new InvalidOperationException("Startup is managed by the selected map's lifecycle action."),
    async (id, ct) => (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == id));
using var serverMetadataGate = new SemaphoreSlim(1, 1);
var serverMetadataCache = new Dictionary<ArkEdition, (SteamBuild Build, DateTimeOffset Fetched)>();
var serverUpdateOperations = new ServerUpdateManagerOperations(repository, operations,
    async (selected, ct) =>
    {
        await serverMetadataGate.WaitAsync(ct);
        try
        {
        if (serverMetadataCache.TryGetValue(selected.Edition, out var cached) && DateTimeOffset.UtcNow - cached.Fetched < TimeSpan.FromMinutes(15))
            return cached.Build;
        var tool = await EnsureSteamCmdAsync(ct);
        var build = await new SteamBuildQuery(tool, Path.Combine(root, "logs", "updates")).QueryAsync(selected.Edition, TimeSpan.FromSeconds(90), ct);
        serverMetadataCache[selected.Edition] = (build, DateTimeOffset.UtcNow);
        return build;
        }
        finally { serverMetadataGate.Release(); }
    }, async (expected, available, ct) =>
    {
        ServerUpdateCheckResult? result = null;
        await lifecycle.WithStatusScopeAsync(async (statuses, token) =>
        {
            var selected = (await repository.ReadAsync(token)).Servers.Single(s => s.Id == expected.Id);
            if (selected.Directory != expected.Directory || selected.Edition != expected.Edition)
                throw new InvalidOperationException("The installation changed while checking its version.");
            var status = statuses.Single(s => s.ServerId == selected.Id);
            var running = status.Managed && status.State == ServerState.Running;
            if (!running && status.State is not (ServerState.Stopped or ServerState.Failed))
                throw new InvalidOperationException("Wait for the selected server's current operation to finish.");
            if (!running && (await new InstallationStopGuard().CheckAsync(selected.Directory, token)).State != InstallationStopState.Stopped)
                throw new InvalidOperationException("The selected installation's stopped state could not be verified.");
            var installed = await ServerUpdateManagerOperations.InstalledBuildAsync(selected, token);
            result = new(selected.Id, installed, available.BuildId,
                await pendingServerUpdates.DetectedAsync(selected.Id, installed, available.BuildId, running, DateTimeOffset.UtcNow, token));
        }, ct);
        return result!;
    });
var announcementOperations = new AnnouncementManagerOperations(repository, operations);
var consoleCommandOperations = new ConsoleCommandManagerOperations(repository, operations);
var worldRestoreOperations = new WorldRestoreManagerOperations(repository, operations, lifecycle, root,
    (server, ct) => new InstallationStopGuard().CheckAsync(server.Directory, ct));
var deletionService = new ServerDeletionService(repository, lifecycle,
    (server, ct) => new InstallationStopGuard().CheckAsync(server.Directory, ct), async (server, ct) =>
    {
        await routerOperations.RemoveForDeletionAsync(server, ct);
        await firewallOperations.RemoveForDeletionAsync(server, ct);
    }, CleanupDeletedQueuesAsync);
async Task CleanupDeletedQueuesAsync(ServerDeletionRecord deletion, CancellationToken ct)
{
    await pendingConfiguration.RemoveDeletedAsync(deletion, ct);
    await pendingAccess.RemoveDeletedAsync(deletion, ct);
    await pendingPresets.RemoveDeletedAsync(deletion, ct);
    await pendingSettings.RemoveDeletedAsync(deletion, ct);
    await pendingModOrder.RemoveDeletedAsync(deletion, ct);
    await pendingServerUpdates.RemoveDeletedAsync(repository, deletion, ct);
    await restartSchedules.RemoveDeletedAsync(deletion, ct);
    await serverStartup.RemoveDeletedAsync(deletion, ct);
    await joiningJournal.RemoveDeletedAsync(await repository.ReadAsync(ct), deletion, ct);
    await new ExistingServerImportJournal(root).RemoveDeletedAsync(await repository.ReadAsync(ct), deletion, ct);
}
string? deletedRecordCleanupProblem = null;
var deletionOperations = new ServerDeletionManagerOperations(repository, deletionService, operations, root,
    async (_, ct) =>
    {
        var current = await repository.ReadAsync(ct);
        return current.Clusters.SelectMany(cluster => ManagedClusterStorage.Resolve(current, cluster.Id, root).ProtectedPaths).ToArray();
    });
var recoveryOperations = new DeletionRecoveryManagerOperations(repository, root, operations, async (record, change, ct) =>
{
    await lifecycle.WithStoppedDeletionRecoveryAsync(record, change,
        (selected, token) => recoveryStopGuard.VerifyAsync(selected, token), ct);
});
var clusterRestoreOperations = new ClusterRestoreManagerOperations(operations, lifecycle, root,
    (selected, token) => new InstallationStopGuard().CheckAsync(selected.Directory, token));
var sharedRestoreOperations = new SharedRestoreManagerOperations(operations, lifecycle, root,
    (server, ct) => new InstallationStopGuard().CheckAsync(server.Directory, ct));
var requestDrain = new ManagerRequestDrain();
Guid? updateShutdownRequest = null;
Task backupWork = Task.CompletedTask, updateWork = Task.CompletedTask, restartWork = Task.CompletedTask;
Action<CancellationToken>? startOwnedWork = null;
var host = new LocalManagerServer(root, async (request, ct) =>
{
    using var requestLease = requestDrain.Enter();
    return request.Operation switch
{
    "ping" => new { Status = "ready", ProtocolVersion = LocalManagerProtocol.Version, ProcessId = Environment.ProcessId },
    "server-update-check-start" => serverUpdateOperations.Start(request),
    "catalog" => await repository.ReadAsync(ct),
    "server-process-status" => RegisteredProcessStatus.FromScan(await repository.ReadAsync(ct),
        await new ServerProcessDiscovery().ScanAsync(ct), OperatingSystem.IsLinux()),
    "server-players-read" => await ServerPlayerQuery.ReadAsync(repository,
        request.Payload?.Deserialize<ServerPlayerQueryRequest>() ?? throw new ArgumentException("Choose a server."), ct),
    "server-announcement-start" => announcementOperations.Start(request),
    "server-console-command-start" => consoleCommandOperations.Start(request),
    "server-log-tail" => await ServerLogTail.ReadAsync(repository, request.Payload!.Value.Deserialize<Guid>(), ct),
    "server-mod-order-save-start" => modOrderOperations.Start(request),
    "server-mod-order-read" => await pendingModOrder.ReadAsync(request.Payload?.Deserialize<Guid>()
        ?? throw new ArgumentException("Choose a server mod list."), ct),
    "server-deletion-start" => deletionOperations.Start(request),
    "server-deletion-record" => await ServerDeletionRecovery.ReadRecordedAsync(repository, root,
        request.Payload?.Deserialize<Guid>() ?? throw new ArgumentException("Choose a recorded deletion."), ct)
        ?? throw new ArgumentException("This deletion has no recorded transaction yet."),
    "server-installation-start" => installationOperations.Start(request),
    "server-creation-publish-start" => creationOperations.StartPublication(request),
    "server-creation-configure-start" => creationOperations.StartConfiguration(request),
    "server-creation-guided-configure-start" => creationOperations.StartGuidedConfiguration(request),
    "server-installation-inventory" => await installationOperations.InventoryAsync(ct),
    "server-installation-record" => await ServerInstallationPreparation.ReadRecordedAsync(root,
        request.Payload?.Deserialize<Guid>() ?? throw new ArgumentException("Choose a recorded setup."), ct),
    "server-creation-check" => await ServerCreationPreflight.CheckAsync(repository, root,
        request.Payload?.Deserialize<ServerCreationCheckRequest>() ?? throw new ArgumentException("Review a new server setup."), ct),
    "deletion-recovery-status" => await ServerDeletionRecovery.ScanAsync(repository, root, ct),
    "deletion-recovery-start" => recoveryOperations.Start(request),
    "server-configuration-read" => await ServerConfigurationReader.ReadAsync(repository,
        request.Payload?.Deserialize<ServerConfigurationReadRequest>()
            ?? throw new ArgumentException("Choose a server configuration file."), ct),
    "server-welcome-save-start" => welcomeOperations.Start(request),
    "server-basic-settings-read" => await basicSettingsOperations.ReadAsync(
        request.Payload?.Deserialize<Guid>() ?? throw new ArgumentException("Choose a server."), ct),
    "server-basic-settings-save-start" => basicSettingsOperations.Start(request),
    "server-access-read" => await pendingAccess.ReadAsync(request.Payload?.Deserialize<Guid>()
        ?? throw new ArgumentException("Choose a server."), ct),
    "server-access-save-start" => accessOperations.Start(request),
    "server-access-discard-start" => accessOperations.StartDiscard(request),
    "server-restart-schedule-read" => await restartSchedules.ReadAsync(request.Payload!.Value.Deserialize<Guid>(), ct),
    "server-restart-schedule-save-start" => restartScheduleOperations.Start(request),
    "cluster-defaults-read" => await clusterDefaultsOperations.ReadAsync(request.Payload!.Value.Deserialize<Guid>(), ct),
    "cluster-defaults-save-start" => clusterDefaultsOperations.Start(request),
    "cluster-membership-read" => await clusterMembershipOperations.ReadAsync(ct),
    "cluster-joining-read" => (await joiningJournal.ReadAsync(ct)).Select(r => new ClusterJoiningStatus(
        r.OperationId, r.Target.Id, r.Target.Name, r.Originals.Select(s => s.Id).ToList())).ToList(),
    "cluster-joining-resume-start" => operations.Submit(request.Id, "cluster-joining-recovery", request.Payload, async (progress, token) =>
    {
        var id = request.Payload?.Deserialize<Guid>() ?? throw new ArgumentException("Choose the recorded joining request.");
        if (id == Guid.Empty) throw new ArgumentException("Choose the recorded joining request.");
        var recorded = (await joiningJournal.ReadAsync(token)).Any(r => r.OperationId == id);
        if (!recorded) return new ClusterJoiningRecoveryResult(id, false);
        progress(new("Checking stopped servers and finishing the recorded joining request."));
        var completed = false;
        await lifecycle.WithStatusScopeAsync(async (_, ownedToken) => completed = await joiningTransaction.ResumeAsync(id, ownedToken), token);
        return new ClusterJoiningRecoveryResult(id, completed);
    }),
    "local-network-addresses" => LocalNetworkAddresses.Read(),
    "router-discovery" => await RouterDiscovery.ReadAsync(ct),
    "router-forwarding-setup-start" => routerOperations.Start(request),
    "router-forwarding-record" => await routerOperations.StatusAsync(request.Payload!.Value.Deserialize<Guid>(), ct),
    "router-forwarding-remove-start" => routerOperations.Remove(request),
    "server-files-repair-start" => repairOperations.Start(request),
    "cluster-overrides-read" => await clusterOverrideOperations.ReadAsync(request.Payload?.Deserialize<Guid>() ?? throw new ArgumentException("Choose a map."), ct),
    "cluster-overrides-save-start" => clusterOverrideOperations.Start(request),
    "server-files-repair-record" => await repairOperations.ReadAsync(request.Payload?.Deserialize<Guid>() ?? throw new ArgumentException("Choose a server."), ct),
    "cluster-preset-review" => await clusterPresetOperations.ReviewAsync(request.Payload!.Value.Deserialize<ClusterPresetSelection>()!, ct),
    "cluster-preset-save-start" => clusterPresetOperations.Start(request),
    "server-firewall-setup-start" => firewallOperations.Start(request),
    "server-firewall-recorded" => await firewallOperations.ReadRecordedAsync(request.Payload?.Deserialize<Guid>() ?? throw new ArgumentException("Choose a server."), ct),
    "ase-client-access-save-start" => aseClientOperations.Start(request),
    "asa-client-access-save-start" => asaClientOperations.Start(request),
    "cluster-membership-save-start" => clusterMembershipOperations.Start(request),
    "server-configuration-save-start" => configurationOperations.Start(request),
    "server-configuration-discard-start" => configurationOperations.StartDiscard(request),
    "server-configuration-pending-review" => await pendingConfiguration.ReviewAsync(request.Payload?.Deserialize<PendingConfigurationReviewRequest>()
        ?? throw new ArgumentException("Choose a saved edit."), ct),
    "server-configuration-resolution-review" => await pendingConfiguration.ReviewResolutionAsync(request.Payload?.Deserialize<PendingConfigurationReviewRequest>()
        ?? throw new ArgumentException("Choose a saved edit."), ct),
    "server-configuration-resolve-start" => configurationOperations.StartResolution(request),
    "server-configuration-pending" => await pendingConfiguration.ReadAsync(request.Payload?.Deserialize<Guid>() ?? throw new ArgumentException("Choose a map."), ct),
    "linux-asa-tools-start" => operations.Submit(request.Id, "linux-asa-tools", request.Payload, async (progress, token) =>
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("ASA Linux tools require Linux.");
        if (request.Payload?.Deserialize<bool>() != true) throw new ArgumentException("Confirm missing compatibility-tool installation.");
        return await new LinuxAsaCompatibilitySetup(root, installationHttp).EnsureToolsAsync(progress, token);
    }),
    "linux-notification-tools-start" => operations.Submit(request.Id, "linux-notification-tools", request.Payload, async (progress, token) =>
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux notification tools require Linux.");
        if (request.Payload?.Deserialize<bool>() != true) throw new ArgumentException("Confirm installation of missing notification tools.");
        var result = await new LinuxRequiredToolSetup(root, notifications: true).EnsureAsync(progress, token);
        return result with { Notice = result.Ready ? "Desktop notification libraries are ready. Your desktop controls alert delivery." : result.Notice };
    }),
    "linux-backup-tools-start" => operations.Submit(request.Id, "linux-backup-tools", request.Payload, async (progress, token) =>
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux backup tools require Linux.");
        if (request.Payload?.Deserialize<bool>() != true) throw new ArgumentException("Confirm installation of missing backup tools.");
        return await new LinuxBackupToolSetup(root).EnsureAsync(progress, token);
    }),
    "windows-backup-prepare" => OperatingSystem.IsWindows()
        ? await snapshotBroker!.InitializeAsync(true, ct)
        : throw new PlatformNotSupportedException("Windows backup permissions require Windows."),
    "resources" => ResourceChecks.Measure(root),
    "activity" => await activity.ReadRecentAsync(100, ct: ct),
    "backups-history" => await ReadBackupHistoryAsync(request, ct),
    "cluster-backups-history" => await ReadClusterBackupHistoryAsync(request, ct),
    "coordinated-backups-history" => await ReadCoordinatedBackupHistoryAsync(request, ct),
    "server-world-restore-start" => worldRestoreOperations.Start(request),
    "coordinated-world-restore-start" => worldRestoreOperations.StartCoordinated(request, root),
    "world-restore-rollback-start" => worldRestoreOperations.StartRollback(request),
    "shared-transfer-restore-start" => sharedRestoreOperations.Start(request),
    "shared-transfer-rollback-start" => sharedRestoreOperations.StartRollback(request),
    "shared-transfer-restore-records" => await ReadSharedRestoreRecordsAsync(request, ct),
    "cluster-world-restore-start" => clusterRestoreOperations.Start(request),
    "cluster-restore-recovery-start" => clusterRestoreOperations.Recover(request),
    "cluster-restore-records" => await ReadClusterRestoreRecordsAsync(request, ct),
    "server-world-restore-records" => await WorldRestoreRecovery.ReadAsync(
        (await repository.ReadAsync(ct)).Servers.Single(s => s.Id ==
            (request.Payload?.Deserialize<Guid>() ?? throw new ArgumentException("Choose a server."))), ct),
    "backup-verify-start" => operations.Submit(request.Id, "backup-verification", request.Payload, async (progress, token) =>
    {
        var selection = request.Payload?.Deserialize<BackupVerificationRequest>()
            ?? throw new ArgumentException("Choose a recorded restore point.");
        progress(new("Checking archive contents and file checksums."));
        return await BackupVerification.VerifyAsync(repository, selection, token);
    }),
    "cluster-backup-verify-start" => operations.Submit(request.Id, "shared-backup-verification", request.Payload, async (progress, token) =>
    {
        var selection = request.Payload?.Deserialize<SharedBackupVerificationRequest>()
            ?? throw new ArgumentException("Choose a recorded shared restore point.");
        progress(new("Checking shared archive contents and file checksums."));
        return await BackupVerification.VerifySharedAsync(repository, root, selection, token);
    }),
    "coordinated-backup-verify-start" => operations.Submit(request.Id, "coordinated-backup-verification", request.Payload, async (progress, token) =>
    {
        var selection = request.Payload?.Deserialize<CoordinatedArchiveRequest>()
            ?? throw new ArgumentException("Choose a recorded coordinated archive.");
        progress(new("Checking the selected archive and coordinated capture identity."));
        await fileOperationGate.WaitAsync(token);
        try { return await CoordinatedArchiveSelection.VerifyAsync(await repository.ReadAsync(token), root, selection, token); }
        finally { fileOperationGate.Release(); }
    }),
    "discover" => await DefaultInstallationSearch.SearchAsync(root, ct: ct),
    "installation-evaluate" => EvaluateInstallation(request),
    "installation-configuration-summary" => await ReadInstallationSummaryAsync(request, ct),
    "existing-import-review" => await existingImportReviews.ReadAsync(request.Payload?.Deserialize<ExistingServerImportReviewRequest>()
        ?? throw new ArgumentException("Choose an existing installation to review."), ct),
    "existing-import-in-place-start" => operations.Submit(request.Id, "existing-server-registration", request.Payload, async (progress, token) =>
    {
        var selection = request.Payload?.Deserialize<ExistingServerRegistrationSelection>() ?? throw new ArgumentException("Review the existing server before import.");
        ServerDefinition? imported = null;
        progress(new("Checking stopped state and registering the existing server without transferring files."));
        await lifecycle.WithStatusScopeAsync(async (_, ownedToken) => imported = await existingRegistration.RegisterInPlaceAsync(selection, ownedToken), token);
        return new ClusterMemberReview(imported!.Id, imported.Edition, imported.Directory, imported.ClusterId);
    }),
    "existing-import-pending" => (await new ExistingServerImportJournal(root).ReadAsync(ct)).Select(record =>
        new ExistingServerImportStatus(record.Server.Id, record.Server.Settings.Name, record.Selection.Mode,
            record.Selection.Registration.Expected.Directory, record.Server.Directory, record.Proof is not null)).ToArray(),
    "existing-import-recover-start" => operations.Submit(request.Id, "existing-server-import-recovery", request.Payload, async (progress, token) =>
    {
        var selection = request.Payload?.Deserialize<ExistingServerImportRecoveryRequest>() ?? throw new ArgumentException("Choose an unfinished import.");
        ExistingServerTransferRegistrationResult? imported = null;
        progress(new("Checking the recorded import and completing registration."));
        await lifecycle.WithStatusScopeAsync(async (_, ownedToken) => imported = await existingRegistration.RecoverPublishedAsync(root, selection, ownedToken), token);
        return imported!;
    }),
    "existing-import-transfer-start" => operations.Submit(request.Id, "existing-server-transfer", request.Payload, async (progress, token) =>
    {
        var selection = request.Payload?.Deserialize<ExistingServerTransferSelection>() ?? throw new ArgumentException("Review the transfer before import.");
        ExistingServerTransferRegistrationResult? imported = null;
        progress(new("Checking the existing installation and transferring its files before registration."));
        await lifecycle.WithStatusScopeAsync(async (_, ownedToken) => imported = await existingRegistration.TransferAndRegisterAsync(selection, root, ownedToken), token);
        return imported!;
    }),
    "installation-stop-check" => await CheckInstallationStoppedAsync(request, ct),
    "installation-destination-check" => CheckInstallationDestination(request),
    "running-installations" => await new ServerProcessDiscovery().ScanAsync(ct),
    "discover-start" => operations.Submit(request.Id, "discovery", request.Payload, async (progress, token) =>
    {
        progress(new("Searching for existing ARK installations."));
        return await DefaultInstallationSearch.SearchAsync(root, ct: token);
    }),
    "operation-status" => ReadOperation(request),
    "operation-status-lookup" => LookupOperation(request),
    "app-update-idle-shutdown" => await ShutdownIdleManagerAsync(request, ct),
    "server-startup-read" => await serverStartup.ReadAsync(ct),
    "server-startup-save" => await serverStartup.SaveAsync(request.Payload?.Deserialize<SaveServerStartupPreferences>()
        ?? throw new ArgumentException("Choose automatic startup preferences."), ct),
    "server-startup-run" => request.Payload is null ? operations.Submit(request.Id, "server-automatic-startup", null, async (progress, token) =>
    {
        var selection = await serverStartup.ReadAsync(token);
        await lifecycle.WithStatusScopeAsync(async (_, ownedToken) =>
        {
            foreach (var join in (await joiningJournal.ReadAsync(ownedToken)).Where(r => selection.Clusters.Contains(r.Target.Id)
                || r.Originals.Any(s => selection.Servers.Contains(s.Id))))
            {
                try { await joiningTransaction.ResumeAsync(join.OperationId, ownedToken); }
                catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
                {
                    await activity.AppendAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "server", ActivitySeverity.Warning,
                        "A recorded cluster join could not finish before automatic startup. Review joining recovery.", OperationId: join.OperationId,
                        Stage: "automatic-server-startup"), ownedToken);
                }
            }
        }, token);
        var current = await repository.ReadAsync(token);
        var targets = selection.Resolve(current);
        var results = new List<MemberOperationResult>();
        foreach (var id in targets)
        {
            progress(new("Starting your selected servers."));
            var latest = await repository.ReadAsync(token);
            if (!selection.Resolve(latest).Contains(id)) continue;
            var outcome = await lifecycle.ExecuteMemberAsync(id, LifecycleAction.Start, token);
            results.AddRange(outcome.Members);
        }
        return new LifecycleOperationResult(results);
    }) : throw new ArgumentException("Automatic startup uses the saved selection."),
    _ when request.Operation.StartsWith("lifecycle-", StringComparison.Ordinal) => await lifecycleOperations.HandleAsync(request, ct),
    "presets-capture" => await CapturePresetAsync(request, ct),
    "presets-application-save-start" => presetApplicationOperations.Start(request),
    "presets-application-discard-start" => presetApplicationOperations.StartDiscard(request),
    "presets-application-pending" => await pendingPresets.ReadStatusAsync(
        request.Payload?.Deserialize<Guid>() ?? throw new ArgumentException("Choose a server."), ct),
    "presets-application-review" => (await presetApplicationReview.PrepareAsync(
        request.Payload?.Deserialize<PresetApplicationSelection>() ?? throw new ArgumentException("Choose a server and preset."), ct)).Review,
    _ when request.Operation.StartsWith("presets-", StringComparison.Ordinal) => await presets.HandleAsync(request, ct),
    _ => throw new ArgumentException("Unknown background-manager operation.")
    };
}, async () =>
{
    await shutdown.CancelAsync();
    try { await operations.DisposeAsync(); }
    finally
    {
        Task<LifecycleOperationResult>? stopWork = null;
        var stopped = await GracefulHostShutdown.RunAsync(token => stopWork = lifecycle.StopAllAsync(token),
            (severity, message) => activity.AppendAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "server", severity, message, Stage: "host-shutdown")),
            TimeSpan.FromMinutes(2));
        if (!stopped.NoticeRecorded) Console.Error.WriteLine("Manager shutdown status could not be recorded. Review running servers before restarting.");
        try
        {
            // Retain manager ownership until canceled stop work has actually settled.
            // The deadline reports uncertainty; it must not permit a second manager
            // to overlap an outstanding request from this one.
            if (stopWork is not null) await stopWork;
        }
        catch (OperationCanceledException) when (stopped.TimedOut) { }
        finally
        {
            try { await Task.WhenAll(backupWork, updateWork, restartWork); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            finally { windowsSession?.Complete(); windowsSession?.Dispose(); windowsSession = null; }
        }
    }
}, async request =>
{
    if (updateShutdownRequest == request.Id) await shutdown.CancelAsync();
}, ownedToken =>
{
    ownedToken.ThrowIfCancellationRequested();
    if (OperatingSystem.IsWindows()) windowsSession = new WindowsSessionShutdown(() => shutdown.Cancel());
    startOwnedWork?.Invoke(ownedToken); return Task.CompletedTask;
});

async Task<IdleManagerShutdownResult> ShutdownIdleManagerAsync(ManagerRequest request, CancellationToken ct)
{
    IdleManagerShutdownResult result = new(false, "The background manager is busy. The installed app will continue running.");
    await lifecycle.WithStatusScopeAsync(async (statuses, token) =>
    {
        if (statuses.Any(s => s.State is not (ServerState.Stopped or ServerState.Failed))) return;
        var current = await repository.ReadAsync(token);
        foreach (var server in current.Servers)
        {
            var stopped = await new InstallationStopGuard().CheckAsync(server.Directory, token);
            if (stopped.State != InstallationStopState.Stopped) return;
        }
        if (!requestDrain.TryBegin(operations)) return;
        updateShutdownRequest = request.Id;
        result = new(true, "The idle manager is closing for the app update.");
    }, ct);
    return result;
}
async Task<AutomaticBackupState> ReadBackupHistoryAsync(ManagerRequest request, CancellationToken ct)
{
    var selection = request.Payload?.Deserialize<BackupHistoryRequest>()
        ?? throw new ArgumentException("Choose a server.");
    var catalog = await repository.ReadAsync(ct);
    var server = catalog.Servers.FirstOrDefault(s => s.Id == selection.ServerId)
        ?? throw new ArgumentException("The selected server is unavailable.");
    return await AutomaticBackupService.ReadRecordedAsync(server, ct);
}

async Task<AutomaticBackupState> ReadClusterBackupHistoryAsync(ManagerRequest request, CancellationToken ct)
{
    var selection = request.Payload?.Deserialize<ClusterBackupHistoryRequest>()
        ?? throw new ArgumentException("Choose a cluster.");
    return await AutomaticBackupService.ReadRecordedClusterAsync(await repository.ReadAsync(ct), selection.ClusterId, root, ct);
}

async Task<CoordinatedBackupHistory> ReadCoordinatedBackupHistoryAsync(ManagerRequest request, CancellationToken ct)
{
    var selection = request.Payload?.Deserialize<CoordinatedBackupHistoryRequest>()
        ?? throw new ArgumentException("Choose a cluster.");
    await fileOperationGate.WaitAsync(ct);
    try { return await CoordinatedBackupRecords.ReadAsync(await repository.ReadAsync(ct), selection.ClusterId, root, ct); }
    finally { fileOperationGate.Release(); }
}

async Task<List<SharedTransferRestoreRecord>> ReadSharedRestoreRecordsAsync(ManagerRequest request, CancellationToken ct)
{
    var clusterId = request.Payload?.Deserialize<Guid>() ?? throw new ArgumentException("Choose a cluster.");
    await fileOperationGate.WaitAsync(ct);
    try { return await SharedTransferRestore.ReadAsync(await repository.ReadAsync(ct), clusterId, root, ct); }
    finally { fileOperationGate.Release(); }
}

async Task<List<ClusterRestoreRecord>> ReadClusterRestoreRecordsAsync(ManagerRequest request, CancellationToken ct)
{
    var clusterId = request.Payload?.Deserialize<Guid>() ?? throw new ArgumentException("Choose a cluster.");
    await fileOperationGate.WaitAsync(ct);
    try { return await ClusterRestoreRecords.ReadAsync(await repository.ReadAsync(ct), clusterId, root, ct); }
    finally { fileOperationGate.Release(); }
}

object LookupOperation(ManagerRequest request)
{
    var details = request.Payload?.Deserialize<OperationStatusRequest>()
        ?? throw new ArgumentException("An operation identity is required.");
    if (details.Id == Guid.Empty) throw new ArgumentException("An operation identity is required.");
    return new OperationLookup(operations.Find(details.Id));
}
object ReadOperation(ManagerRequest request)
{
    var details = request.Payload?.Deserialize<OperationStatusRequest>()
        ?? throw new ArgumentException("An operation identity is required.");
    return operations.Find(details.Id) ?? throw new ArgumentException("The operation result is unavailable or has expired. Its completion is unknown.");
}
object EvaluateInstallation(ManagerRequest request)
{
    var selection = request.Payload?.Deserialize<InstallationEvaluationRequest>()
        ?? throw new ArgumentException("Choose an installation folder.");
    if (string.IsNullOrWhiteSpace(selection.Directory) || !Path.IsPathFullyQualified(selection.Directory))
        throw new ArgumentException("Choose an absolute installation folder.");
    return new InstallationDiscovery().Evaluate(selection.Directory);
}
object CheckInstallationDestination(ManagerRequest request)
{
    var selection = request.Payload?.Deserialize<InstallationDestinationRequest>()
        ?? throw new ArgumentException("Choose source and destination folders.");
    try
    {
        var target = InstallationTransfer.CheckManagedDestination(root, selection.Source, selection.Destination);
        return new InstallationDestinationCheck(target, true,
            "Destination path checked. Server compatibility and stopping checks are still required.");
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    { return new InstallationDestinationCheck(selection.Destination, false, error.Message); }
}
async Task<InstallationStopCheck> CheckInstallationStoppedAsync(ManagerRequest request, CancellationToken ct)
{
    var selection = request.Payload?.Deserialize<InstallationEvaluationRequest>()
        ?? throw new ArgumentException("Choose an installation folder.");
    if (string.IsNullOrWhiteSpace(selection.Directory) || !Path.IsPathFullyQualified(selection.Directory))
        throw new ArgumentException("Choose an absolute installation folder.");
    return await new InstallationStopGuard().CheckAsync(selection.Directory, ct);
}
async Task<InstallationConfigurationSummary> ReadInstallationSummaryAsync(ManagerRequest request, CancellationToken ct)
{
    var selection = request.Payload?.Deserialize<InstallationConfigurationRequest>()
        ?? throw new ArgumentException("Choose an installation and platform.");
    var configuration = await InstallationConfigurationReader.ReadAsync(selection.Directory, selection.Edition, selection.Platform, ct);
    return InstallationConfigurationReader.Summarize(configuration);
}
try
{
    Console.WriteLine("Background manager starting.");
    var snapshotProvider = new WindowsSnapshotSetProvider(Path.Combine(AppContext.BaseDirectory, "WindowsSnapshot",
        OperatingSystem.IsWindows() ? "ArkServerManager.WindowsSnapshot.exe" : "ArkServerManager.WindowsSnapshot"));
    var backupScheduler = new AutomaticBackupScheduler(repository, lifecycle.StatusAsync,
        (server, observation, now, ct) => server.ClusterId is not null
            ? lifecycle.WithBackupWorkAsync(server, (_, _) => throw new InvalidOperationException("Retention does not capture."),
                (_, _) => throw new InvalidOperationException("Retention does not capture."),
                (_, _) => throw new InvalidOperationException("Retention does not capture."),
                (_, token) => LegacyClusterBackupRetention.TickAsync(server, observation, token), ct)
            : lifecycle.WithBackupWorkAsync(server, async (selected, token) =>
            {
                var endpoint = await ServerRconConfiguration.ReadAsync(repository, selected.Id,
                    TimeSpan.FromMinutes(1), RconResponseMode.SinglePacket, token);
                var response = await new RconClient().ExecuteAsync(endpoint, "SaveWorld", token);
                if (!response.Trim().Equals("World Saved", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("ARK's completed world save could not be confirmed; no live backup was captured.");
            }, (source, token) => OperatingSystem.IsWindows() ? snapshotBroker!.AcquireAsync(source, token) : OperatingSystem.IsLinux() ? linuxSnapshots!.AcquireAsync(source, token) : snapshotProvider.AcquireAsync(source, token),
            (selected, token) => new InstallationStopGuard().CheckAsync(selected.Directory, token),
            async (acquireState, token) =>
            {
                var service = new AutomaticBackupService(server, TimeSpan.FromMinutes(1), acquireState,
                    message => RecordBackupNoticeAsync(server.Id, message), SnapshotLayout.WorldSavedFolders);
                if (observation is not null) await service.TickMeasuredAsync(observation, now, token);
                else await service.TickAsync(TimeSpan.Zero, false, now, token);
            }, ct),
        RecordBackupNoticeAsync,
        readClusterRuntime: clusterRunningTime.Runtime,
        executeClusterTick: (document, cluster, observation, now, ct) => lifecycle.WithClusterBackupWorkAsync(cluster.Id, root,
            async (selected, token) =>
            {
                var endpoint = await ServerRconConfiguration.ReadAsync(repository, selected.Id,
                    TimeSpan.FromMinutes(1), RconResponseMode.SinglePacket, token);
                var response = await new RconClient().ExecuteAsync(endpoint, "SaveWorld", token);
                if (!response.Trim().Equals("World Saved", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("ARK's completed world save could not be confirmed; no live cluster backup was captured.");
            }, (sources, token) => OperatingSystem.IsWindows() ? snapshotBroker!.AcquireSetAsync(sources, token) : OperatingSystem.IsLinux() ? linuxSnapshots!.AcquireSetAsync(sources, token) : snapshotProvider.AcquireSetAsync(sources, token),
            (selected, token) => new InstallationStopGuard().CheckAsync(selected.Directory, token),
            async (current, acquire, token) =>
            {
                await ClusterRestoreRecords.RequireConsistentAsync(current, cluster.Id, root, token);
                // Finish already-recorded independent work before the coordinated scheduler takes over.
                // Old per-map clocks are not advanced or relabelled as cluster runtime.
                foreach (var member in current.Servers.Where(s => s.ClusterId == cluster.Id))
                {
                    try
                    {
                        var recorded = await AutomaticBackupService.ReadRecordedAsync(member, token);
                        if (recorded.PendingHour is null && recorded.QueuedHours.Count == 0) continue;
                        var legacy = new AutomaticBackupService(member, TimeSpan.FromMinutes(1),
                            async captureToken => (await acquire(captureToken)).GetServerState(member.Id),
                            message => RecordBackupNoticeAsync(member.Id, message), SnapshotLayout.WorldSavedFolders);
                        await legacy.TickAsync(TimeSpan.Zero, false, now, token);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                        or InvalidOperationException or JsonException)
                    {
                        await RecordBackupNoticeAsync(member.Id, "Previous independent backup needs review: " + error.Message);
                    }
                }
                var service = new AutomaticBackupService(current, cluster.Id, root, TimeSpan.FromMinutes(1),
                    async captureToken => (await acquire(captureToken)).GetSharedState(),
                    message => RecordBackupNoticeAsync(null, $"Cluster '{cluster.Name}': " + message));
                await service.TickMeasuredAsync(observation, now, token);
                var publisher = new CoordinatedArchivePublisher();
                var coordinated = new CoordinatedBackupService(current, cluster.Id, root,
                    (hour, captureToken) => publisher.PublishAsync(current, cluster.Id, root, hour, acquire, captureToken),
                    message => RecordBackupNoticeAsync(null, $"Cluster '{cluster.Name}': " + message),
                    (hour, cleanupToken) => CoordinatedArchiveRetention.ExpireAsync(current, cluster.Id, root, hour, cleanupToken));
                await coordinated.TickAsync(observation, now, token);
            }, ct), maintenance: MaintainBackupStorageAsync);
    startOwnedWork = ownedToken =>
    {
    backupWork = backupScheduler.RunAsync(ownedToken);
    updateWork = new AutomaticServerUpdateScheduler(repository,
        (id, ct) => serverUpdateOperations.CheckAsync(id, _ => { }, ct),
        (id, message, ct) => activity.AppendAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "update",
            ActivitySeverity.Information, message, id == Guid.Empty ? null : id, Stage: "automatic-server-update"), ct))
        .RunAsync(TimeSpan.FromMinutes(15), ownedToken);
    restartWork = restartSchedules.RunAsync(lifecycle.RestartRunningMemberAsync,
        (id, message, ct) => activity.AppendAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "server",
            message.Contains("completed", StringComparison.Ordinal) ? ActivitySeverity.Information : ActivitySeverity.Warning,
            message, id == Guid.Empty ? null : id, Stage: "scheduled-restart"), ct), ownedToken);
    };
    try
    {
        await host.RunAsync(shutdown.Token);
        return 0;
    }
    finally
    {
        await shutdown.CancelAsync();
        try { await backupWork; }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        try { await updateWork; }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        try { await restartWork; }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }

Task RecordBackupNoticeAsync(Guid? serverId, string message) => activity.AppendAsync(new(
    Guid.NewGuid(), DateTimeOffset.UtcNow, "backup", ActivitySeverity.Warning, message,
    serverId, Stage: "automatic-backup"));




async Task CleanupLinuxSnapshotsAsync(CancellationToken ct)
{
    if (!OperatingSystem.IsLinux()) return;
    try
    {
        await linuxSnapshots!.CleanupPendingAsync(ct);
        if (linuxSnapshotCleanupProblem is not null)
            await activity.AppendAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "backup", ActivitySeverity.Information,
                "Temporary Linux snapshot cleanup recovered.", Stage: "automatic-backup"));
        linuxSnapshotCleanupProblem = null;
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or NotSupportedException or TimeoutException or JsonException)
    {
        if (linuxSnapshotCleanupProblem != error.Message)
            await RecordBackupNoticeAsync(null, "Temporary Linux snapshot cleanup needs review: " + error.Message);
        linuxSnapshotCleanupProblem = error.Message;
    }
}




async Task MaintainBackupStorageAsync(CancellationToken ct)
{
    await CleanupLinuxSnapshotsAsync(ct);
    await lifecycle.WithStatusScopeAsync(async (statuses, ct) =>
    {
        await CleanupNativeLaunchPathsAsync(ct);
        try
        {
            await DeletedServerRecordCleanup.CleanupCompletedAsync(repository, root, ct, CleanupDeletedQueuesAsync);
            deletedRecordCleanupProblem = null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (deletedRecordCleanupProblem != error.Message)
                await activity.AppendAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "cleanup", ActivitySeverity.Warning,
                    "Deleted-server associated records require review: " + error.Message, Stage: "server-record-cleanup"));
            deletedRecordCleanupProblem = error.Message;
        }
        var current = await repository.ReadAsync(ct);
        foreach (var server in current.Servers)
        {
            var status = statuses.SingleOrDefault(s => s.ServerId == server.Id);
            if (status is not { Managed: true, State: ServerState.Running }) continue;
            try
            {
                await WorldRestoreRecovery.CleanupAfterVerifiedStartAsync(server, ct);
                restoreCleanupProblems.Remove(server.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or InvalidOperationException or JsonException)
            {
                if (restoreCleanupProblems.GetValueOrDefault(server.Id) != error.Message)
                    await RecordBackupNoticeAsync(server.Id, "Previous restored data for map '" + server.Settings.Name + "' needs cleanup review: " + error.Message);
                restoreCleanupProblems[server.Id] = error.Message;
            }
        }
        foreach (var cluster in current.Clusters)
        {
            try
            {
                await ClusterRestoreRecords.RequireConsistentAsync(current, cluster.Id, root, ct);
                await SharedTransferRestore.CleanupAfterClusterStartAsync(current, cluster.Id, root, clusterRunningTime.AllRunning, ct);
                await ClusterRestoreCleanup.AfterVerifiedStartAsync(current, cluster.Id, root, clusterRunningTime.AllRunning, ct);
                restoreCleanupProblems.Remove(cluster.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or InvalidOperationException or JsonException)
            {
                if (restoreCleanupProblems.GetValueOrDefault(cluster.Id) != error.Message)
                    await RecordBackupNoticeAsync(null, "Previous restored data for cluster '" + cluster.Name + "' needs cleanup review: " + error.Message);
                restoreCleanupProblems[cluster.Id] = error.Message;
            }
        }
    }, ct);
}

async Task CleanupNativeLaunchPathsAsync(CancellationToken ct)
{
    if (nativeLaunchDrives is null) return;
    try { await nativeLaunchDrives.CleanupStoppedAsync(ct); nativeDriveCleanupProblem = null; }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or JsonException)
    {
        if (nativeDriveCleanupProblem != error.Message)
            await activity.AppendAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "server", ActivitySeverity.Warning,
                "Temporary server launch-path cleanup needs review.", Stage: "launch-path-cleanup"));
        nativeDriveCleanupProblem = error.Message;
    }
}


