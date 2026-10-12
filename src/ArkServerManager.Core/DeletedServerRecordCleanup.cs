namespace ArkServerManager.Core;

/// <summary>Removes only explicitly owned per-server records after catalog and file deletion are complete.</summary>
public static class DeletedServerRecordCleanup
{
    public static async Task RemoveAsync(CatalogDocument catalog, ServerDeletionRecord deletion, string managementRoot, CancellationToken ct = default)
    {
        RequireCompleted(catalog, deletion);
        await RemoveRecordsAsync(deletion, managementRoot, ct);
    }
    public static void RequireCompleted(CatalogDocument catalog, ServerDeletionRecord deletion)
    {
        if (deletion.SchemaVersion != 1 || !Path.IsPathFullyQualified(deletion.OriginalDirectory) || !Path.IsPathFullyQualified(deletion.StagedDirectory)
            || deletion.Phase != ServerDeletionPhase.FilesRemoved || deletion.ServerId == Guid.Empty || deletion.OperationId == Guid.Empty
            || catalog.Servers.Any(server => server.Id == deletion.ServerId) || Path.Exists(deletion.OriginalDirectory) || Path.Exists(deletion.StagedDirectory))
            throw new InvalidOperationException("Associated records require a completed server deletion.");
    }
    public static void RequireDirectory(ServerDeletionRecord deletion, string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(deletion.OriginalDirectory)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("A queued record belongs to a different server folder. It was retained for review.");
    }
    private static async Task RemoveRecordsAsync(ServerDeletionRecord deletion, string managementRoot, CancellationToken ct)
    {
        var settings = Path.Combine(Path.GetFullPath(managementRoot), "app-settings");
        var paths = new[] { "repair-", "firewall-", "router-" }.Select(prefix => Path.Combine(settings, prefix + deletion.ServerId.ToString("N") + ".json")).ToArray();
        var creationPath = Path.Combine(settings, "creation", deletion.ServerId.ToString("N") + ".json");
        var publicationPath = Path.Combine(settings, "creation-publication", deletion.ServerId.ToString("N") + ".json");
        var lockPath = creationPath + ".lock";
        foreach (var path in new[] { creationPath, publicationPath, lockPath }) RejectLinks(path);
        FileStream? ownership = null;
        if (Path.Exists(creationPath) || Path.Exists(publicationPath) || Path.Exists(lockPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        await using var setupOwnership = ownership;
        foreach (var path in paths) RejectLinks(path);
        bool Matches(Guid id, string directory) => id == deletion.ServerId && Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(deletion.OriginalDirectory)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var repair = await CoordinatedBackupRecords.ReadBoundedAsync<ServerRepairRecord>(paths[0], ct);
        var firewall = await CoordinatedBackupRecords.ReadBoundedAsync<FirewallSetupRecord>(paths[1], ct);
        var router = await CoordinatedBackupRecords.ReadBoundedAsync<RouterSetupRecord>(paths[2], ct);
        var creation = await CoordinatedBackupRecords.ReadBoundedAsync<ServerInstallationRecord>(creationPath, ct);
        var publication = await CoordinatedBackupRecords.ReadBoundedAsync<ServerCreationPublicationRecord>(publicationPath, ct);
        if (repair is not null && (repair.SchemaVersion != 1 || !Matches(repair.ServerId, repair.Directory) || repair.OperationId == Guid.Empty || !Enum.IsDefined(repair.Phase))
            || firewall is not null && (firewall.SchemaVersion != 1 || !Matches(firewall.ServerId, firewall.Directory) || !firewall.Removed)
            || router is not null && (router.SchemaVersion != 1 || !Matches(router.ServerId, router.Directory) || router.OperationId == Guid.Empty || router.Phase != RouterSetupPhase.Removed)
            || creation is not null && (creation.SchemaVersion != 1 || !Matches(creation.ServerId, creation.FinalDirectory) || creation.Phase != ServerInstallationPhase.FilesReady
                || !Enum.IsDefined(creation.Edition) || !Enum.IsDefined(creation.Platform) || Path.Exists(creation.StagingDirectory))
            || publication is not null && (publication.SchemaVersion != 1 || publication.ServerId != deletion.ServerId || publication.Phase != ServerCreationPublicationPhase.Registered))
            throw new InvalidDataException("Associated record ownership or network removal needs review. Records were retained.");
        if (OperatingSystem.IsLinux()) await LinuxCompatibilityPrefixCleanup.RemoveAsync(managementRoot, deletion.ServerId, ct);
        foreach (var path in paths.Concat([creationPath, publicationPath])) { ct.ThrowIfCancellationRequested(); RejectLinks(path); if (File.Exists(path)) File.Delete(path); }
        if (ownership is not null)
        {
            await ownership.DisposeAsync();
            RejectLinks(lockPath); File.Delete(lockPath);
        }
    }
    public static async Task CleanupCompletedAsync(CatalogRepository repository, string managementRoot, CancellationToken ct = default,
        Func<ServerDeletionRecord, CancellationToken, Task>? cleanupQueues = null)
    {
        var folder = Path.Combine(Path.GetFullPath(managementRoot), "app-settings", "deletions");
        RejectLinks(folder);
        if (!Directory.Exists(folder)) return;
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (++count > 1024) throw new IOException("Too many deletion records; review cleanup history.");
            var name = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(name, "N", out var operation) || name != operation.ToString("N"))
                throw new InvalidDataException("An unexpected deletion record requires review.");
            var status = await ServerDeletionRecovery.ReadRecordedAsync(repository, managementRoot, operation, ct);
            if (status is { Record.Phase: ServerDeletionPhase.FilesRemoved, Assessment.Action: DeletionRecoveryAction.None })
            {
                await RemoveAsync(await repository.ReadAsync(ct), status.Record, managementRoot, ct);
                if (cleanupQueues is not null) await cleanupQueues(status.Record, ct);
            }
        }
    }
    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked associated records require review.");
    }
}
