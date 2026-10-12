using System.Collections.Concurrent;

namespace ArkServerManager.Core;

public enum RunningUpdateChoice { StopUpdateRestart, UpdateWhenStopped }
public sealed record QueuedServerUpdate(Guid ServerId, ulong BuildId, DateTimeOffset DetectedAt)
{
    public string? Directory { get; init; }
    public ArkEdition? Edition { get; init; }
}
public sealed record UpdateQueueDocument(int SchemaVersion, List<QueuedServerUpdate> Updates);
public sealed record ServerUpdateResult(bool Updated, bool Queued, bool Restarted, string Message);

/// <summary>Applies running-server consent and stopped-server automation using explicit host callbacks.</summary>
public sealed class ServerUpdateWorkflow
{
    private readonly AtomicJsonStore<UpdateQueueDocument> store;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim gate;
    private readonly Func<Guid, CancellationToken, Task<RunningUpdateChoice>> prompt;
    private readonly Func<Guid, CancellationToken, Task<bool>> stop;
    private readonly Func<Guid, ulong, CancellationToken, Task<bool>> update;
    private readonly Func<Guid, CancellationToken, Task> start;
    private readonly Func<Guid, CancellationToken, Task<ServerDefinition>>? registration;

    public async Task RemoveDeletedAsync(CatalogRepository repository, ServerDeletionRecord deletion, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            DeletedServerRecordCleanup.RequireCompleted(await repository.ReadAsync(ct), deletion);
            var document = await LoadAsync(ct);
            var entry = document.Updates.SingleOrDefault(p => p.ServerId == deletion.ServerId);
            if (entry is null) return;
            if (entry.Directory is not null) DeletedServerRecordCleanup.RequireDirectory(deletion, entry.Directory);
            await store.SaveAsync(document with { Updates = document.Updates.Where(p => p.ServerId != deletion.ServerId).ToList() }, ct);
        }
        finally { gate.Release(); }
    }

    public ServerUpdateWorkflow(string queueFile, Func<Guid, CancellationToken, Task<RunningUpdateChoice>> prompt,
        Func<Guid, CancellationToken, Task<bool>> stop, Func<Guid, ulong, CancellationToken, Task<bool>> update,
        Func<Guid, CancellationToken, Task> start,
        Func<Guid, CancellationToken, Task<ServerDefinition>>? registration = null)
    {
        store = new(queueFile);
        gate = Gates.GetOrAdd(Path.GetFullPath(queueFile), _ => new(1, 1));
        this.prompt = prompt; this.stop = stop; this.update = update; this.start = start;
        this.registration = registration;
    }

    public async Task<ServerUpdateResult> DetectedAsync(Guid id, ulong installedBuild, ulong availableBuild, bool running,
        DateTimeOffset now, CancellationToken ct = default)
    {
        if (id == Guid.Empty || availableBuild == 0) throw new ArgumentException("Valid server and build identities are required.");
        bool alreadyQueued;
        await gate.WaitAsync(ct);
        try
        {
            var queue = await LoadAsync(ct);
            if (installedBuild == availableBuild)
            {
                if (queue.Updates.Any(u => u.ServerId == id))
                    await store.SaveAsync(queue with { Updates = queue.Updates.Where(u => u.ServerId != id).ToList() }, ct);
                return new(false, false, false, "Server is up to date.");
            }
            var current = registration is null ? null : await registration(id, ct);
            if (current is not null && (current.Id != id || !Path.IsPathFullyQualified(current.Directory) || !Enum.IsDefined(current.Edition)))
                throw new InvalidDataException("The registered update installation could not be verified.");
            alreadyQueued = queue.Updates.Any(u => u.ServerId == id && u.BuildId == availableBuild
                && (current is null || u.Directory == current.Directory && u.Edition == current.Edition));
            await store.SaveAsync(queue with
                { Updates = [.. queue.Updates.Where(u => u.ServerId != id), new(id, availableBuild, now)
                    { Directory = current?.Directory, Edition = current?.Edition }] }, ct);
        }
        finally { gate.Release(); }
        if (!running) return await OnStoppedAsync(id, ct);
        if (alreadyQueued) return new(false, true, false, "Update remains queued until the server stops.");
        // Lifecycle stop/start callbacks may re-enter the queued-update workflow.
        var choice = await prompt(id, ct);
        if (!Enum.IsDefined(choice)) throw new InvalidDataException("Invalid update response.");
        if (choice == RunningUpdateChoice.UpdateWhenStopped)
            return new(false, true, false, "Update queued until the next stop.");
        if (!await stop(id, ct)) return new(false, true, false, "Shutdown failed; the update remains queued.");
        var result = await OnStoppedAsync(id, ct);
        if (result.Queued) return result;
        await start(id, ct);
        return new(true, false, true, "Server updated and restart requested.");
    }
    /// <summary>Call only after verified shutdown, before a manually requested restart.</summary>
    public async Task<ServerUpdateResult> OnStoppedAsync(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var queued = (await LoadAsync(ct)).Updates.SingleOrDefault(u => u.ServerId == id);
            if (queued is not null && registration is not null)
            {
                var current = await registration(id, ct);
                if (current.Id != id || queued.Directory is null || queued.Edition is null
                    || current.Directory != queued.Directory || current.Edition != queued.Edition)
                    throw new InvalidOperationException("The queued update belongs to a different or unverified installation. Check updates again before starting.");
            }
            return queued is null ? new(false, false, false, "No update is queued.") : await ApplyAsync(id, queued.BuildId, false, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<ServerUpdateResult> ApplyAsync(Guid id, ulong build, bool restart, CancellationToken ct)
    {
        if (!await update(id, build, ct)) return new(false, true, false, "Update failed and remains queued; the server was left stopped.");
        var queue = await LoadAsync(ct);
        await store.SaveAsync(queue with { Updates = queue.Updates.Where(u => u.ServerId != id).ToList() }, ct);
        if (restart) await start(id, ct);
        return new(true, false, restart, restart ? "Server updated and restart requested." : "Stopped server updated.");
    }

    private async Task<UpdateQueueDocument> LoadAsync(CancellationToken ct)
    {
        var queue = await store.LoadAsync(ct) ?? new(1, []);
        if (queue.SchemaVersion != 1 || queue.Updates is null || queue.Updates.Any(u => u is null || u.ServerId == Guid.Empty || u.BuildId == 0
                || (u.Directory is null) != (u.Edition is null) || u.Directory is not null && !Path.IsPathFullyQualified(u.Directory)
                || u.Edition is ArkEdition edition && !Enum.IsDefined(edition))
            || queue.Updates.GroupBy(u => u.ServerId).Any(g => g.Count() > 1)) throw new InvalidDataException("Invalid server update queue.");
        return queue;
    }
}
