using System.Text.Json;
using ArkServerManager.Core;

internal static class NativeWorldBackupSmoke
{
    public static async Task<object> RunAsync(string evidence, CancellationToken ct)
    {
        var repository = new CatalogRepository(Path.Combine(evidence, "catalog.json"));
        var server = (await repository.ReadAsync(ct)).Servers.Single();
        if (server.Edition != ArkEdition.ASE || server.ClusterId is not null || server.Mods.Count != 0)
            throw new InvalidDataException("Choose the standalone ASE verification catalog.");
        async Task RequireStopped(CancellationToken token)
        {
            if ((await new InstallationStopGuard().CheckAsync(server.Directory, token)).State != InstallationStopState.Stopped)
                throw new IOException("The verification installation is not stopped.");
        }
        await RequireStopped(ct);
        var saved = Path.Combine(server.Directory, "ShooterGame", "Saved");
        var world = Path.Combine(saved, "SavedArks");
        if (!Directory.GetFiles(world, "*.ark").Any()) throw new IOException("A real saved world is required.");
        var marker = Path.Combine(world, ".asm-backup-verification.txt");
        var archivePath = Path.Combine(evidence, "native-world-runtime-1.zip");
        if (File.Exists(marker) || File.Exists(archivePath)) throw new IOException("This verification has existing state. Inspect it before repeating.");
        var settings = Path.Combine(saved, "Config", "WindowsServer", "GameUserSettings.ini");
        var originalSettings = await File.ReadAllBytesAsync(settings, ct);
        await File.WriteAllTextAsync(marker, "before backup", ct);
        var archive = new SnapshotArchive();
        var manifest = await archive.CreateAsync(saved, archivePath, server.Id, server.Edition, 1, ct,
            layout: SnapshotLayout.WorldSavedFolders);
        await archive.VerifyAsync(archivePath, ct);
        if (manifest.Files.Any(f => f.Path.StartsWith("Config/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("World backup unexpectedly contains settings.");
        await File.WriteAllTextAsync(marker, "after backup", ct);
        var service = new WorldRestoreService(async token => { await RequireStopped(token); return true; },
            _ => throw new InvalidOperationException("Startup is verified separately."),
            async (work, token) => { await RequireStopped(token); await work(token); });
        var operation = Guid.NewGuid();
        var restored = await service.RestoreAsync(server, archivePath, 1, operation, restart: false, ct);
        var markerRestored = await File.ReadAllTextAsync(marker, ct) == "before backup";
        var restoredSettings = await File.ReadAllBytesAsync(settings, ct);
        var settingsPreserved = originalSettings.SequenceEqual(restoredSettings);
        if (!restored.Restored || !markerRestored || !settingsPreserved) throw new InvalidDataException("Native world restoration did not preserve its expected state.");
        var previousRetained = restored.PreviousStateDirectory is not null && Directory.Exists(restored.PreviousStateDirectory);
        await NativeAseSmoke.RunAsync(server.Directory, evidence, ct, resume: true,
            afterReady: (definition, token) => WorldRestoreRecovery.CleanupAfterVerifiedStartAsync(definition, token));
        var record = (await WorldRestoreRecovery.ReadAsync(server, ct)).Single(r => r.OperationId == operation);
        var result = new { ArchiveFiles = manifest.Files.Count, ArchiveVerified = true, WorldMarkerRestored = markerRestored,
            SettingsPreservedDuringRestore = settingsPreserved, PreviousRetainedBeforeStartup = previousRetained,
            PreviousRemovedAfterVerifiedStartup = record.Phase == WorldRestorePhase.Cleaned && !Directory.Exists(record.PreviousDirectory),
            NativeStopped = (await new InstallationStopGuard().CheckAsync(server.Directory, ct)).State == InstallationStopState.Stopped,
            RunningSnapshotCaptureVerified = false, GameplayConnectionVerified = false };
        await File.WriteAllTextAsync(Path.Combine(evidence, "world-backup-restore-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), ct);
        return result;
    }
}
