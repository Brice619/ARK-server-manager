using System.Text.Json;

namespace ArkServerManager.Core;

public static class WorldRestoreRecovery
{
    public static async Task<IReadOnlyList<WorldRestoreRecord>> ReadAsync(ServerDefinition server, CancellationToken ct = default)
    {
        var folder = Path.Combine(Path.GetFullPath(server.Directory), "backups", "restore-transactions");
        RejectLinks(folder);
        if (!Directory.Exists(folder)) return [];
        var paths = Directory.EnumerateFiles(folder, "*.json").Take(1001).ToArray();
        if (paths.Length > 1000) throw new InvalidDataException("Too many restore records require review.");
        var records = new List<WorldRestoreRecord>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested(); RejectLinks(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 65536) throw new InvalidDataException("A restore record exceeds its supported size.");
            var record = await JsonSerializer.DeserializeAsync<WorldRestoreRecord>(input, cancellationToken: ct)
                ?? throw new InvalidDataException("The restore record is empty.");
            if (record.OperationId == Guid.Empty || record.ServerId != server.Id || record.Edition != server.Edition
                || record.RuntimeHour < 1 || !Enum.IsDefined(record.Phase)
                || record.OriginalWorldFolders is { } originals && (originals.Any(folder => folder is not ("SavedArks" or "SaveGames"))
                    || originals.Distinct().Count() != originals.Count)
                || Path.GetFileName(path) != record.OperationId.ToString("N") + ".json"
                || !Same(record.SavedDirectory, Path.Combine(server.Directory, "ShooterGame", "Saved"))
                || !Same(record.StagingDirectory, Path.Combine(server.Directory, ".world-restore-stage-" + record.OperationId.ToString("N")))
                || !Same(record.PreviousDirectory, Path.Combine(server.Directory, ".world-restore-previous-" + record.OperationId.ToString("N"))))
                throw new InvalidDataException("The restore record does not match this installation.");
            records.Add(record);
        }
        return records;
    }

    public static async Task RequireConsistentAsync(ServerDefinition server, CancellationToken ct = default)
    {
        if ((await ReadAsync(server, ct)).Any(record => record.Phase is WorldRestorePhase.Replacing or WorldRestorePhase.RollingBack))
            throw new InvalidOperationException("An interrupted world replacement requires recovery before this map can start.");
    }

    /// <summary>Caller must hold the lifecycle gate and have confirmed the selected map's successful startup.</summary>
    public static async Task CleanupAfterVerifiedStartAsync(ServerDefinition server, CancellationToken ct = default)
    {
        var records = await ReadAsync(server, ct);
        if (records.Any(record => record.Phase is WorldRestorePhase.Replacing or WorldRestorePhase.RollingBack))
            throw new InvalidOperationException("Interrupted replacement must be recovered before cleanup.");
        foreach (var record in records.Where(record => record.Phase is WorldRestorePhase.Replaced or WorldRestorePhase.Started))
        {
            var journal = new AtomicJsonStore<WorldRestoreRecord>(Path.Combine(server.Directory, "backups", "restore-transactions", record.OperationId.ToString("N") + ".json"));
            var started = record with { Phase = WorldRestorePhase.Started };
            await journal.SaveAsync(started, ct);
            RejectTree(record.PreviousDirectory);
            if (Directory.Exists(record.PreviousDirectory)) Directory.Delete(record.PreviousDirectory, true);
            await journal.SaveAsync(started with { Phase = WorldRestorePhase.Cleaned }, ct);
        }
    }

    /// <summary>Caller holds a verified stopped-member file scope. Returns to pre-restore world state only.</summary>
    public static async Task RollbackInterruptedAsync(ServerDefinition server, Guid operationId, CancellationToken ct = default)
    {
        var record = (await ReadAsync(server, ct)).SingleOrDefault(r => r.OperationId == operationId)
            ?? throw new ArgumentException("Choose a recorded restore operation.");
        if (record.Phase == WorldRestorePhase.RolledBack)
        {
            CleanupRollbackFiles(record);
            return;
        }
        if (record.Phase is not (WorldRestorePhase.Replacing or WorldRestorePhase.RollingBack))
            throw new InvalidOperationException("This restore is not an interrupted replacement.");
        if (record.OriginalWorldFolders is null)
            throw new InvalidOperationException("This older restore lacks original-folder inventory and requires explicit recovery review.");
        RejectTree(record.SavedDirectory); RejectTree(record.StagingDirectory); RejectTree(record.PreviousDirectory);
        var roots = new[] { "SavedArks", "SaveGames" };
        foreach (var folder in roots)
        {
            var current = Path.Combine(record.SavedDirectory, folder);
            var previous = Path.Combine(record.PreviousDirectory, folder);
            var staged = Path.Combine(record.StagingDirectory, folder);
            if (File.Exists(current) || File.Exists(previous) || File.Exists(staged))
                throw new IOException("A recovery world-folder path is occupied by a file.");
            var original = record.OriginalWorldFolders.Contains(folder);
            if ((!original && Directory.Exists(previous))
                || Directory.Exists(current) && Directory.Exists(previous) && Directory.Exists(staged)
                || original && !Directory.Exists(previous) && !Directory.Exists(current)
                || !original && Directory.Exists(current) && Directory.Exists(staged))
                throw new InvalidDataException("World recovery locations are inconsistent; no rollback was started.");
        }
        var journal = new AtomicJsonStore<WorldRestoreRecord>(Path.Combine(server.Directory, "backups", "restore-transactions", operationId.ToString("N") + ".json"));
        record = record with { Phase = WorldRestorePhase.RollingBack };
        await journal.SaveAsync(record, ct);
        Directory.CreateDirectory(record.StagingDirectory); Directory.CreateDirectory(record.SavedDirectory);
        foreach (var folder in roots)
        {
            ct.ThrowIfCancellationRequested();
            var current = Path.Combine(record.SavedDirectory, folder);
            var previous = Path.Combine(record.PreviousDirectory, folder);
            var staged = Path.Combine(record.StagingDirectory, folder);
            if (Directory.Exists(previous))
            {
                if (Directory.Exists(current)) Directory.Move(current, staged);
                Directory.Move(previous, current);
            }
            else if (!record.OriginalWorldFolders.Contains(folder) && Directory.Exists(current)) Directory.Move(current, staged);
        }
        await journal.SaveAsync(record with { Phase = WorldRestorePhase.RolledBack }, CancellationToken.None);
        CleanupRollbackFiles(record);
    }

    private static void CleanupRollbackFiles(WorldRestoreRecord record)
    {
        RejectTree(record.StagingDirectory); RejectTree(record.PreviousDirectory);
        if (Directory.Exists(record.PreviousDirectory) && Directory.EnumerateFileSystemEntries(record.PreviousDirectory).Any())
            throw new InvalidDataException("Rolled-back restore still has previous-state files requiring review.");
        if (Directory.Exists(record.StagingDirectory)) Directory.Delete(record.StagingDirectory, true);
        if (Directory.Exists(record.PreviousDirectory)) Directory.Delete(record.PreviousDirectory);
    }

    private static bool Same(string? first, string second) => first is not null && Path.IsPathFullyQualified(first)
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked restore paths require review.");
    }
    private static void RejectTree(string path)
    {
        RejectLinks(path); if (!Directory.Exists(path)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        { RejectLinks(entry); if (Directory.Exists(entry)) RejectTree(entry); }
    }
}
