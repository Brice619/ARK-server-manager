using System.Collections.Concurrent;

namespace ArkServerManager.Core;

public enum WorldRestorePhase { Prepared, Replacing, Replaced, RolledBack, Started, Cleaned, RollingBack }
public sealed record WorldRestoreRecord(Guid OperationId, Guid ServerId, ArkEdition Edition, long RuntimeHour,
    string SavedDirectory, string StagingDirectory, string PreviousDirectory, WorldRestorePhase Phase)
{
    public List<string>? OriginalWorldFolders { get; init; }
}

/// <summary>Replaces only world/player folders. Callbacks must use a selected-member lifecycle scope.</summary>
public sealed class WorldRestoreService(
    Func<CancellationToken, Task<bool>> stop,
    Func<CancellationToken, Task> start,
    Func<Func<CancellationToken, Task>, CancellationToken, Task> withStoppedFiles)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly string[] Roots = ["SavedArks", "SaveGames"];

    public async Task<RestoreResult> RestoreAsync(ServerDefinition server, string backupFile, long runtimeHour,
        Guid operationId, bool restart, CancellationToken ct = default, SnapshotCapture? expectedCapture = null)
    {
        if (!Path.IsPathFullyQualified(server.Directory) || operationId == Guid.Empty || runtimeHour < 1)
            throw new ArgumentException("Choose a registered installation and restore point.");
        var saved = Path.Combine(Path.GetFullPath(server.Directory), "ShooterGame", "Saved");
        var staging = Path.Combine(server.Directory, ".world-restore-stage-" + operationId.ToString("N"));
        var previous = Path.Combine(server.Directory, ".world-restore-previous-" + operationId.ToString("N"));
        var journalPath = Path.Combine(server.Directory, "backups", "restore-transactions", operationId.ToString("N") + ".json");
        RejectLinks(saved); RejectLinks(staging); RejectLinks(previous); RejectLinks(journalPath);
        var gate = Gates.GetOrAdd(saved, _ => new(1, 1));
        await gate.WaitAsync(ct);
        var cleanStaging = false;
        try
        {
            await WorldRestoreRecovery.RequireConsistentAsync(server, ct);
            if (File.Exists(journalPath) || Directory.Exists(staging) || Directory.Exists(previous))
                throw new InvalidOperationException("This restore has already been attempted. Review its recorded outcome before continuing.");
            var manifest = await new SnapshotArchive().ExtractToNewDirectoryAsync(backupFile, staging, server.Id,
                server.Edition, ct, expectedRuntimeHour: runtimeHour);
            cleanStaging = true;
            if (expectedCapture is not null && manifest.Capture != expectedCapture)
                throw new InvalidDataException("The world archive does not belong to the selected coordinated capture.");
            if (manifest.Layout != SnapshotLayout.WorldSavedFolders)
                throw new InvalidDataException("This archive has no verified world-folder layout. Review its inventory before restoring.");
            foreach (var folder in Roots) Directory.CreateDirectory(Path.Combine(staging, folder));
            var record = new WorldRestoreRecord(operationId, server.Id, server.Edition, runtimeHour, saved, staging, previous, WorldRestorePhase.Prepared);
            var store = new AtomicJsonStore<WorldRestoreRecord>(journalPath);
            await store.SaveAsync(record, ct);
            if (!await stop(ct)) return new(false, false, null, "The selected map did not stop; world files were not replaced.");
            var invoked = false;
            await withStoppedFiles(async token =>
            {
                if (invoked) throw new InvalidOperationException("The restore scope was entered twice.");
                invoked = true; token.ThrowIfCancellationRequested();
                RejectTree(saved); RejectTree(staging); RejectLinks(previous);
                foreach (var folder in Roots)
                    if (File.Exists(Path.Combine(saved, folder))) throw new IOException("A world-folder location is occupied by a file.");
                record = record with { Phase = WorldRestorePhase.Replacing,
                    OriginalWorldFolders = Roots.Where(folder => Directory.Exists(Path.Combine(saved, folder))).ToList() };
                await store.SaveAsync(record, token);
                Directory.CreateDirectory(saved); Directory.CreateDirectory(previous);
                cleanStaging = false;
                var replaced = new List<(string Folder, bool HadPrevious, bool Installed)>();
                try
                {
                    foreach (var folder in Roots)
                    {
                        var current = Path.Combine(saved, folder); var old = Path.Combine(previous, folder);
                        var hadPrevious = Directory.Exists(current);
                        replaced.Add((folder, hadPrevious, false));
                        if (hadPrevious) Directory.Move(current, old);
                        Directory.Move(Path.Combine(staging, folder), current);
                        replaced[^1] = (folder, hadPrevious, true);
                    }
                    // Once replacement begins, record its outcome even if the client/job token is canceled.
                    record = record with { Phase = WorldRestorePhase.Replaced };
                    await store.SaveAsync(record, CancellationToken.None);
                    cleanStaging = true;
                }
                catch
                {
                    foreach (var entry in replaced.AsEnumerable().Reverse())
                    {
                        var current = Path.Combine(saved, entry.Folder); var old = Path.Combine(previous, entry.Folder);
                        if (entry.Installed) Directory.Move(current, Path.Combine(staging, entry.Folder));
                        if (entry.HadPrevious && Directory.Exists(old)) Directory.Move(old, current);
                    }
                    record = record with { Phase = WorldRestorePhase.RolledBack };
                    await store.SaveAsync(record, CancellationToken.None);
                    cleanStaging = true;
                    throw;
                }
            }, ct);
            if (!invoked) throw new InvalidOperationException("The stopped-map scope did not authorize restoration.");
            if (!restart) return new(true, false, previous, "World restored. Previous world state is retained until verified startup.");
            try { await start(ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            { return new(true, false, previous, "World restored, but startup failed: " + error.Message); }
            if ((await store.LoadAsync(CancellationToken.None))?.Phase == WorldRestorePhase.Cleaned)
                return new(true, true, null, "World restored and the selected map started. Settings were preserved.");
            record = record with { Phase = WorldRestorePhase.Started };
            await store.SaveAsync(record, CancellationToken.None);
            try
            {
                RejectTree(previous); Directory.Delete(previous, true);
                record = record with { Phase = WorldRestorePhase.Cleaned };
                await store.SaveAsync(record, CancellationToken.None);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return new(true, true, Directory.Exists(previous) ? previous : null, "World restored and started; previous-state cleanup needs attention."); }
            return new(true, true, null, "World restored and the selected map started. Settings were preserved.");
        }
        finally
        {
            try
            {
                if (cleanStaging && Directory.Exists(staging)) { RejectTree(staging); Directory.Delete(staging, true); }
            }
            finally { gate.Release(); }
        }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked restore paths require explicit handling.");
    }
    private static void RejectTree(string path)
    {
        RejectLinks(path);
        if (!Directory.Exists(path)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        { RejectLinks(entry); if (Directory.Exists(entry)) RejectTree(entry); }
    }
}
