namespace ArkServerManager.Core;

public enum ServerDeletionPhase { Prepared, Staged, CatalogRemoved, FilesRemoved, RolledBack, RollingBack, RemovingFiles }
public sealed record ServerDeletionRecord(int SchemaVersion, Guid OperationId, Guid ServerId, string ServerName,
    string OriginalDirectory, string StagedDirectory, Guid? PreservedClusterId,
    IReadOnlyList<string> ProtectedSharedPaths, ServerDeletionPhase Phase);

/// <summary>Initial deletion transaction; startup recovery must be connected before exposing this to UI.</summary>
public static class ServerDeletionTransaction
{
    public static async Task<ServerDeletionRecord> ExecuteAsync(CatalogRepository repository, Guid operationId,
        Guid serverId, string confirmedName, string managementRoot, IReadOnlyList<string> sharedPaths,
        Func<ServerDefinition, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedFiles,
        CancellationToken ct = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A deletion identity is required.");
        ArgumentNullException.ThrowIfNull(withStoppedFiles);
        var initial = await repository.ReadAsync(ct);
        var selected = initial.Servers.SingleOrDefault(s => s.Id == serverId)
            ?? throw new ArgumentException("Choose an existing server.");
        if (!string.Equals(confirmedName, selected.Settings.Name, StringComparison.Ordinal))
            throw new ArgumentException("Enter the exact server name before deletion.");
        var plan = ServerDeletionPlan.Create(initial, serverId, managementRoot, sharedPaths);
        var parent = Path.GetDirectoryName(plan.ServerDirectory)!;
        var staged = Path.Combine(parent, ".ark-delete-" + operationId.ToString("N"));
        var journalPath = Path.Combine(Path.GetFullPath(managementRoot), "app-settings", "deletions", operationId.ToString("N") + ".json");
        CheckRegularTree(plan.ServerDirectory);
        RejectLinks(journalPath);
        if (Directory.Exists(staged) || File.Exists(staged) || File.Exists(journalPath))
            throw new InvalidOperationException("This deletion has existing recovery state. Recover it before trying again.");
        var record = new ServerDeletionRecord(1, operationId, serverId, confirmedName, plan.ServerDirectory,
            staged, plan.PreservedClusterId, plan.ProtectedSharedPaths, ServerDeletionPhase.Prepared);
        var store = new AtomicJsonStore<ServerDeletionRecord>(journalPath);
        var invoked = false;
        await withStoppedFiles(selected, async token =>
        {
            if (invoked) throw new InvalidOperationException("Deletion was authorized more than once.");
            invoked = true;
            await repository.ChangeAsync(async (catalog, commitToken) =>
            {
                var current = catalog.Servers.SingleOrDefault(s => s.Id == serverId);
                if (current is null || current.Directory != selected.Directory || current.Edition != selected.Edition
                    || current.ClusterId != selected.ClusterId || current.Settings.Name != confirmedName)
                    throw new InvalidOperationException("The server changed. Review deletion again.");
                ServerDeletionPlan.Create(new(1, [.. catalog.Clusters], [.. catalog.Servers]), serverId, managementRoot, record.ProtectedSharedPaths);
                CheckRegularTree(record.OriginalDirectory);
                RejectLinks(journalPath);
                await store.SaveAsync(record, commitToken);
                Directory.Move(record.OriginalDirectory, record.StagedDirectory);
                record = record with { Phase = ServerDeletionPhase.Staged };
                await store.SaveAsync(record, commitToken);
                catalog.RemoveServer(serverId);
            }, token);
            record = record with { Phase = ServerDeletionPhase.CatalogRemoved };
            await store.SaveAsync(record, token);
            // Validate the exact staged tree again before recursive removal.
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(record.StagedDirectory)), parent,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("Deletion staging location changed.");
            CheckRegularTree(record.StagedDirectory);
            Directory.Delete(record.StagedDirectory, recursive: true);
            record = record with { Phase = ServerDeletionPhase.FilesRemoved };
            await store.SaveAsync(record, token);
        }, ct);
        if (!invoked) throw new InvalidOperationException("The stopped-file scope did not authorize deletion.");
        return record;
    }

    private static void CheckRegularTree(string directory)
    {
        RejectLinks(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The server deletion folder is unavailable.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinks(entry);
            if (Directory.Exists(entry)) CheckRegularTree(entry);
        }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked deletion locations require separate validation.");
    }
}
