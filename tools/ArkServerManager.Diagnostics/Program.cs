using System.Text.Json;
using ArkServerManager.Core;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    object result;
    if (args is ["windows-shortcut-check", var shortcutApplication, var shortcutEvidence])
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(shortcutEvidence) || Path.Exists(shortcutEvidence))
            throw new ArgumentException("Choose a new absolute Windows shortcut evidence folder.");
        Directory.CreateDirectory(shortcutEvidence);
        var link = Path.Combine(shortcutEvidence, "ARK Server Manager.lnk");
        WindowsAppShortcuts.CreateAt(shortcutApplication, [link]);
        WindowsAppShortcuts.CreateAt(shortcutApplication, [link]);
        var conflict = Path.Combine(shortcutEvidence, "existing.lnk"); var withheld = Path.Combine(shortcutEvidence, "withheld.lnk");
        await File.WriteAllTextAsync(conflict, "preserve existing shortcut", cancellation.Token);
        var refused = false;
        try { WindowsAppShortcuts.CreateAt(shortcutApplication, [withheld, conflict]); }
        catch (IOException) { refused = true; }
        if (!refused || File.Exists(withheld) || await File.ReadAllTextAsync(conflict, cancellation.Token) != "preserve existing shortcut")
            throw new IOException("Conflicting shortcut preservation failed.");
        result = new { ShortcutCreatedAndReadBack = true, RepeatedCreationVerified = true, ConflictingFilePreserved = true,
            EntireSelectionPreflightVerified = true, UserMenuChanged = false, UserDesktopChanged = false, Shortcut = link };
        await File.WriteAllTextAsync(Path.Combine(shortcutEvidence, "verification.json"), JsonSerializer.Serialize(result), cancellation.Token);
    }
    else if (args is ["launcher-recovery-smoke", var launcherFile, var launcherFixture, var launcherEvidence])
        result = await LauncherRecoverySmoke.RunAsync(launcherFile, launcherFixture, launcherEvidence, cancellation.Token);
    else if (args is ["firewall-script-check", var firewallEvidence])
        result = await FirewallScriptCheck.RunAsync(firewallEvidence, cancellation.Token);
    else if (args is ["windows-server-runtime-check", var runtimeFolder, var runtimeEdition] && Enum.TryParse<ArkEdition>(runtimeEdition, out var runtimeGame))
        result = WindowsServerRuntimeLibraries.Check(new(Guid.NewGuid(), runtimeGame, "TheIsland", Path.GetFullPath(runtimeFolder), new()));
    else if (args is ["ase-installed-mod-check", var installedModEvidence])
    {
        var selected = (await new CatalogRepository(Path.Combine(installedModEvidence, "catalog.json")).ReadAsync(cancellation.Token)).Servers.Single();
        await AseInstalledModFiles.RequireAsync(selected, cancellation.Token);
        result = new { SelectedMods = selected.Mods, InstalledFileCheckPassed = true, GameplayVerified = false };
        await File.WriteAllTextAsync(Path.Combine(installedModEvidence, "installed-file-check.json"), JsonSerializer.Serialize(result), cancellation.Token);
    }
    else if (args is ["ase-mod-identity-check", var aseIdentityDirectory, var aseIdentityMod, var aseIdentityEvidence])
    {
        var evidencePath = Path.GetFullPath(aseIdentityEvidence);
        var selected = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.GetFullPath(aseIdentityDirectory), new()) { Mods = [aseIdentityMod] };
        await AseInstalledModFiles.RequireAsync(selected, cancellation.Token);
        result = new { ServerDirectory = selected.Directory, SelectedMods = selected.Mods, DescriptorIdentityVerified = true,
            UnpackedAssetPresenceVerified = true, GameplayVerified = false };
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        await using var output = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, result, cancellationToken: cancellation.Token);
    }
    else if (args is ["umu-bootstrap-check", var umuDirectory])
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ARKServerManager-Verification/1.0");
        var bootstrap = new UmuBootstrap(http);
        var launcher = await bootstrap.EnsureAsync(umuDirectory, cancellation.Token);
        var rechecked = await bootstrap.EnsureAsync(umuDirectory, cancellation.Token);
        result = new { UmuBootstrap.Version, Launcher = launcher, ExistingLauncherReused = rechecked == launcher, LinuxExecutionVerified = false };
    }
    else if (args is ["native-ase-cluster-smoke", var clusterRoot, var clusterEvidence])
        result = await NativeClusterSmoke.RunAsync(clusterRoot, clusterEvidence, cancellation.Token);
    else if (args is ["native-ase-managed-drive-smoke", var aseDriveSource, var aseDriveEvidence, var aseDriveMod])
    {
        var selected = (await new CatalogRepository(Path.Combine(aseDriveSource, "catalog.json")).ReadAsync(cancellation.Token)).Servers.Single();
        var backend = new NativeWindowsLaunchDriveBackend();
        var drives = new WindowsLaunchDrives(Path.Combine(aseDriveEvidence + "-launch-path", "drives.json"), backend,
            async (directory, ct) => (await new InstallationStopGuard().CheckAsync(directory, ct)).State == InstallationStopState.Stopped);
        var alias = await drives.GetOrCreateAsync(selected.Directory, cancellation.Token);
        try { result = await NativeAseSmoke.RunAseModAsync(aseDriveSource, aseDriveEvidence, aseDriveMod, cancellation.Token, alias); }
        finally { await drives.CleanupStoppedAsync(CancellationToken.None); }
        await File.WriteAllTextAsync(Path.Combine(aseDriveEvidence, "managed-drive-result.json"), JsonSerializer.Serialize(new
            { MappingRemovedAfterNativeStop = backend.Query(alias[0]).Count == 0, DataMoved = false }), cancellation.Token);
    }
    else if (args is ["native-asa-managed-drive-smoke", var managedDriveSource, var managedDriveEvidence, var managedDriveMod])
    {
        var selected = (await new CatalogRepository(Path.Combine(managedDriveSource, "catalog.json")).ReadAsync(cancellation.Token)).Servers.Single();
        var backend = new NativeWindowsLaunchDriveBackend();
        var drives = new WindowsLaunchDrives(Path.Combine(managedDriveEvidence + "-launch-path", "drives.json"), backend,
            async (directory, ct) => (await new InstallationStopGuard().CheckAsync(directory, ct)).State == InstallationStopState.Stopped);
        var alias = await drives.GetOrCreateAsync(selected.Directory, cancellation.Token);
        try { result = await NativeAseSmoke.RunAsaModAsync(managedDriveSource, managedDriveEvidence, managedDriveMod,
            cancellation.Token, alternateLaunchRoot: alias); }
        finally { await drives.CleanupStoppedAsync(CancellationToken.None); }
        await File.WriteAllTextAsync(Path.Combine(managedDriveEvidence, "managed-drive-result.json"), JsonSerializer.Serialize(new
            { MappingRemovedAfterNativeStop = backend.Query(alias[0]).Count == 0, DataMoved = false }), cancellation.Token);
    }
    else if (args is ["native-launch-drive-smoke", var driveServer, var driveEvidence])
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(driveServer) || !Path.IsPathFullyQualified(driveEvidence))
            throw new ArgumentException("Choose absolute Windows verification paths.");
        if (Directory.Exists(driveEvidence) || File.Exists(driveEvidence)) throw new IOException("Choose a new drive evidence folder.");
        var backend = new NativeWindowsLaunchDriveBackend();
        var drives = new WindowsLaunchDrives(Path.Combine(driveEvidence, "drives.json"), backend,
            async (directory, ct) => (await new InstallationStopGuard().CheckAsync(directory, ct)).State == InstallationStopState.Stopped);
        var alias = await drives.GetOrCreateAsync(driveServer, cancellation.Token);
        var resolved = backend.Resolve(alias);
        var reused = await new WindowsLaunchDrives(Path.Combine(driveEvidence, "drives.json"), backend,
            async (directory, ct) => (await new InstallationStopGuard().CheckAsync(directory, ct)).State == InstallationStopState.Stopped)
            .GetOrCreateAsync(driveServer, cancellation.Token);
        await drives.CleanupStoppedAsync(cancellation.Token);
        result = new { Alias = alias, SamePhysicalDirectory = resolved.Equals(backend.Resolve(driveServer), StringComparison.OrdinalIgnoreCase),
            ReloadReusedOwnedMapping = reused == alias, MappingRemovedAfterVerifiedStop = backend.Query(alias[0]).Count == 0,
            DataMoved = false };
        await File.WriteAllTextAsync(Path.Combine(driveEvidence, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), cancellation.Token);
    }
    else if (args is ["native-world-backup-smoke", var worldEvidence])
        result = await NativeWorldBackupSmoke.RunAsync(worldEvidence, cancellation.Token);
    else if (args is ["native-configuration-check", var configurationEvidence])
        result = await NativeAseSmoke.VerifyConfigurationAsync(configurationEvidence, cancellation.Token);
    else if (args is [var nativeAction, var nativeServer, var nativeEvidence] && nativeAction is "native-ase-smoke" or "native-ase-smoke-resume" or "native-ase-smoke-generated")
        result = await NativeAseSmoke.RunAsync(nativeServer, nativeEvidence, cancellation.Token, nativeAction != "native-ase-smoke", nativeAction == "native-ase-smoke-generated");
    else if (args is ["native-asa-smoke", var asaServer, var asaEvidence])
        result = await NativeAseSmoke.RunAsync(asaServer, asaEvidence, cancellation.Token, edition: ArkEdition.ASA);
    else if (args is ["native-ase-mod-smoke", var aseSourceEvidence, var aseModEvidence, var aseModId])
        result = await NativeAseSmoke.RunAseModAsync(aseSourceEvidence, aseModEvidence, aseModId, cancellation.Token);
    else if (args is ["native-asa-mod-smoke", var asaSourceEvidence, var asaModEvidence, var asaModId])
        result = await NativeAseSmoke.RunAsaModAsync(asaSourceEvidence, asaModEvidence, asaModId, cancellation.Token);
    else if (args is ["native-asa-mod-directory-smoke", var asaDirectorySource, var asaDirectoryEvidence, var asaDirectoryMod])
        result = await NativeAseSmoke.RunAsaModAsync(asaDirectorySource, asaDirectoryEvidence, asaDirectoryMod, cancellation.Token, overrideModsDirectory: true);
    else if (args is ["native-asa-mod-drive-smoke", var asaDriveSource, var asaDriveEvidence, var asaDriveMod, var asaDriveRoot])
        result = await NativeAseSmoke.RunAsaModAsync(asaDriveSource, asaDriveEvidence, asaDriveMod, cancellation.Token, alternateLaunchRoot: asaDriveRoot);
    else if (args is ["native-asa-mod-remove-smoke", var asaRemovalSource, var asaRemovalEvidence])
        result = await NativeAseSmoke.RunAsaRemovalAsync(asaRemovalSource, asaRemovalEvidence, cancellation.Token);
    else if (args is ["windows-snapshot-smoke", var snapshotHelper, var snapshotEvidence, var permissionMode]
        && permissionMode is "--elevate" or "--existing-privileges")
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This check requires Windows.");
        result = await WindowsSnapshotSmoke.RunAsync(snapshotHelper, snapshotEvidence, permissionMode == "--elevate", cancellation.Token);
    }
    else if (args is ["install-payload", var payloadFolder, var installationFolder, var payloadRuntime])
        result = await VerifiedPayloadInstaller.InstallAsync(payloadFolder, installationFolder, payloadRuntime, cancellation.Token);
    else if (args is ["stage-app-update", var updatePackage, var stagingParent, var updateRuntime])
    {
        var staged = await AppUpdateStaging.ExtractAsync(updatePackage, stagingParent, updateRuntime, cancellation.Token);
        result = new { staged.Directory, staged.Manifest.Runtime, Files = staged.Manifest.Files.Count };
    }
    else if (args is ["manager", var recoveryRoot, "deletion-recovery-start", var recoveryIdentity, var recoveryServer,
        var confirmedName, var recoveryAction, var requestIdentity]
        && Guid.TryParse(recoveryIdentity, out var recoveryId) && Guid.TryParse(recoveryServer, out var recoveryServerId)
        && Guid.TryParse(requestIdentity, out var recoveryRequestId) && Enum.TryParse<DeletionRecoveryAction>(recoveryAction, out var selectedRecovery))
        result = await new LocalManagerClient(recoveryRoot).CallAsync<BackgroundOperation>("deletion-recovery-start",
            new DeletionRecoverySelection(recoveryId, recoveryServerId, confirmedName, selectedRecovery),
            TimeSpan.FromSeconds(15), cancellation.Token, recoveryRequestId);
    else if (args is ["manager", var backupRoot, "backup-verify-start", var backupServerText, var backupHourText, var backupOperationText]
        && Guid.TryParse(backupServerText, out var backupServerId) && long.TryParse(backupHourText, out var backupHour)
        && Guid.TryParse(backupOperationText, out var backupOperationId))
        result = await new LocalManagerClient(backupRoot).CallAsync<BackgroundOperation>("backup-verify-start",
            new BackupVerificationRequest(backupServerId, backupHour), TimeSpan.FromSeconds(15), cancellation.Token, backupOperationId);
    else if (args is ["manager", var configurationRoot, "server-configuration-read", var serverIdentity, var configurationName]
        && Guid.TryParse(serverIdentity, out var configurationServerId))
    {
        var configuration = await new LocalManagerClient(configurationRoot).CallAsync<ServerConfigurationFile>("server-configuration-read",
            new ServerConfigurationReadRequest(configurationServerId, configurationName), TimeSpan.FromSeconds(15), cancellation.Token);
        // Verification output never dumps configuration values or passwords.
        result = new { configuration.ServerId, configuration.FileName, configuration.Platform,
            Found = configuration.Text is not null, Characters = configuration.Text?.Length,
            TextSHA256 = configuration.Text is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(configuration.Text))).ToLowerInvariant() };
    }
    else if (args is ["manager", var summaryRoot, "installation-configuration-summary", var summaryFolder, var summaryEdition, var summaryPlatform]
        && Enum.TryParse<ArkEdition>(summaryEdition, out var importedEdition))
        result = await new LocalManagerClient(summaryRoot).CallAsync<InstallationConfigurationSummary>("installation-configuration-summary",
            new InstallationConfigurationRequest(summaryFolder, importedEdition, summaryPlatform), TimeSpan.FromSeconds(15), cancellation.Token);
    else if (args is ["manager", var importRoot, "installation-destination-check", var source, var target])
        result = await new LocalManagerClient(importRoot).CallAsync<InstallationDestinationCheck>("installation-destination-check",
            new InstallationDestinationRequest(source, target), TimeSpan.FromSeconds(15), cancellation.Token);
    else if (args is ["manager", var stopRoot, "installation-stop-check", var installation])
        result = await new LocalManagerClient(stopRoot).CallAsync<InstallationStopCheck>("installation-stop-check",
            new InstallationEvaluationRequest(installation), TimeSpan.FromSeconds(15), cancellation.Token);
    else if (args is ["manager", var statusRoot, "operation-status", var operationId] && Guid.TryParse(operationId, out var parsedId))
        result = await new LocalManagerClient(statusRoot).CallAsync<JsonElement>("operation-status", new OperationStatusRequest(parsedId), TimeSpan.FromSeconds(15), cancellation.Token);
    else if (args is ["manager", var dataRoot, var operation])
        result = await new LocalManagerClient(dataRoot).CallAsync<JsonElement>(operation, null, TimeSpan.FromSeconds(15), cancellation.Token);
    else if (args is ["workshop", var id])
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        result = await new SteamWorkshopMetadata(http).GetAsync(id, cancellation.Token);
    }
    else if (args is ["bootstrap", var platform, var destination] && Enum.TryParse<ServerPlatform>(platform, true, out var serverPlatform))
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        result = await new SteamCmdBootstrap(http).EnsureAsync(destination, serverPlatform, cancellation.Token);
    }
    else if (args is [var steamAction, var installTool, var installEdition, var installPlatform, var serverFolder, var installLogs]
        && steamAction is "steam-install" or "steam-update"
        && Enum.TryParse<ArkEdition>(installEdition, true, out var downloadEdition)
        && Enum.TryParse<ServerPlatform>(installPlatform, true, out var downloadPlatform))
        result = await new SteamCmdInstaller(installTool, installLogs).InstallOrUpdateAsync(
            new(downloadEdition, downloadPlatform, serverFolder, steamAction == "steam-install"), cancellationToken: cancellation.Token);
    else if (args is ["steam-query", var steamExecutable, var queryEdition, var queryLogs] && Enum.TryParse<ArkEdition>(queryEdition, true, out var selectedEdition))
        result = await new SteamBuildQuery(steamExecutable, queryLogs).QueryAsync(selectedEdition, TimeSpan.FromSeconds(60), cancellation.Token);
    else if (args is ["steam-build", var metadataFile, var editionText] && Enum.TryParse<ArkEdition>(editionText, true, out var edition))
        result = SteamBuildMetadata.ReadAvailable(await File.ReadAllTextAsync(metadataFile, cancellation.Token), SteamCmdInstaller.AppId(edition));
    else if (args is ["auto-search", var dedicated]) result = await DefaultInstallationSearch.SearchAsync(dedicated, ct: cancellation.Token);
    else if (args is ["evaluate", var folder]) result = new InstallationDiscovery().Evaluate(folder);
    else if (args is ["search", .. var roots] && roots.Length > 0)
        result = await new InstallationDiscovery().SearchAsync(roots, cancellation.Token);
    else
    {
        Console.Error.WriteLine("Development diagnostics: workshop <ASE mod ID> | evaluate <server folder> | search <folders...> | bootstrap <Windows|Linux> <new tool folder> | steam-build <metadata file> <ASE|ASA>");
        return 2;
    }
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("The operation was canceled or timed out."); return 1; }
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
