namespace ArkServerManager.Core;

public sealed record RestoreResult(bool Restored, bool Restarted, string? PreviousStateDirectory, string Message);

/// <summary>Restores a selected server state folder after verification and successful shutdown.</summary>
public sealed class ServerRestoreService
{
    private readonly SnapshotArchive archive = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<CancellationToken, Task<bool>> stop;
    private readonly Func<CancellationToken, Task> start;
    private readonly Func<Func<CancellationToken, Task>, CancellationToken, Task> withStoppedFiles;
    public ServerRestoreService(Func<CancellationToken, Task<bool>> stop, Func<CancellationToken, Task> start,
        Func<Func<CancellationToken, Task>, CancellationToken, Task> withStoppedFiles)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(withStoppedFiles);
        this.stop = stop; this.start = start; this.withStoppedFiles = withStoppedFiles;
    }

    public async Task<RestoreResult> RestoreAsync(string backupFile, string stateDirectory, Guid serverId,
        ArkEdition edition, bool restart, bool retainPreviousState, CancellationToken ct = default, long? expectedRuntimeHour = null)
    {
        var state = Path.GetFullPath(stateDirectory);
        RejectLinkedAncestors(state);
        var parent = Path.GetDirectoryName(state);
        if (parent is null) throw new ArgumentException("A server state folder is required.");
        var staging = Path.Combine(parent, ".restore-stage-" + Guid.NewGuid().ToString("N"));
        var previous = Path.Combine(parent, ".restore-previous-" + Guid.NewGuid().ToString("N"));
        await gate.WaitAsync(ct);
        try
        {
            // Complete extraction and checksum validation before interrupting a running world.
            await archive.ExtractToNewDirectoryAsync(backupFile, staging, serverId, edition, ct,
                expectedRuntimeHour: expectedRuntimeHour);
            if (Directory.Exists(state) && (File.GetAttributes(state) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked state folders require explicit restore handling.");
            if (!await stop(ct)) return new(false, false, null, "The server did not stop; its state was left unchanged.");
            ct.ThrowIfCancellationRequested();
            var hadPrevious = false;
            var invoked = false;
            await withStoppedFiles(token =>
            {
                if (invoked) throw new InvalidOperationException("The restore scope authorized replacement more than once.");
                invoked = true;
                token.ThrowIfCancellationRequested();
                RejectLinkedAncestors(state);
                if (Directory.Exists(state) && (File.GetAttributes(state) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked state folders require explicit restore handling.");
                hadPrevious = Directory.Exists(state);
                if (hadPrevious) Directory.Move(state, previous);
                try { Directory.Move(staging, state); }
                catch
                {
                    if (hadPrevious && !Directory.Exists(state)) Directory.Move(previous, state);
                    throw;
                }
                return Task.CompletedTask;
            }, ct);
            if (!invoked) throw new InvalidOperationException("The stopped-map scope did not authorize restoration.");
            if (restart)
            {
                try { await start(ct); }
                catch (Exception error) when (error is not OperationCanceledException)
                { return new(true, false, hadPrevious ? previous : null, "State restored, but restart failed: " + error.Message); }
                // User policy: retain the replaced state until verified startup succeeds.
                if (hadPrevious && !retainPreviousState)
                {
                    RejectLinkedTree(previous);
                    Directory.Delete(previous, true);
                }
            }
            return new(true, restart, hadPrevious && (!restart || retainPreviousState) ? previous : null,
                "Selected server state restored.");
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            finally { gate.Release(); }
        }
    }

    private static void RejectLinkedAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked restore locations require explicit handling.");
    }

    private static void RejectLinkedTree(string directory)
    {
        RejectLinkedAncestors(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinkedAncestors(entry);
            if (Directory.Exists(entry)) RejectLinkedTree(entry);
        }
    }
}
