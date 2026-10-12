using System.Collections.Concurrent;

namespace ArkServerManager.Core;

public sealed record PendingModOrder(Guid ServerId, List<string> Order)
{
    public bool Replace { get; init; }
    public string? SourceDirectory { get; init; }
    public List<string>? PreviousOrder { get; init; }
    public ArkEdition? SourceEdition { get; init; }
}
public sealed record PendingModOrders(int SchemaVersion, List<PendingModOrder> Changes);
public sealed record ModOrderPending(Guid ServerId, List<string>? Order, bool Replace = false);

/// <summary>The host must serialize lifecycle actions with this workflow and supply verified running state.</summary>
public sealed class ModOrderWorkflow
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly CatalogRepository repository;
    private readonly AtomicJsonStore<PendingModOrders> pending;
    private readonly SemaphoreSlim gate;
    private readonly Func<IReadOnlyList<Guid>, CancellationToken, Task<bool>> stop;
    private readonly Func<IReadOnlyList<Guid>, CancellationToken, Task> start;
    private readonly Func<Guid, IReadOnlyList<string>, CancellationToken, Task> write;
    private readonly Func<Guid, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedApply;
    private readonly Func<Guid, Func<CancellationToken, Task>, CancellationToken, Task> withRunningQueue;
    private readonly Func<ServerDefinition, IReadOnlyList<string>, CancellationToken, Task>? removeFiles;
    private readonly bool supportsAsaRemoval;

    public async Task RemoveDeletedAsync(ServerDeletionRecord deletion, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            DeletedServerRecordCleanup.RequireCompleted(await repository.ReadAsync(ct), deletion);
            var document = await ReadPendingAsync(ct);
            foreach (var entry in document.Changes.Where(p => p.ServerId == deletion.ServerId && p.SourceDirectory is not null))
                DeletedServerRecordCleanup.RequireDirectory(deletion, entry.SourceDirectory!);
            if (!document.Changes.Any(p => p.ServerId == deletion.ServerId)) return;
            await pending.SaveAsync(document with { Changes = document.Changes.Where(p => p.ServerId != deletion.ServerId).ToList() }, ct);
        }
        finally { gate.Release(); }
    }

    public ModOrderWorkflow(CatalogRepository repository, string pendingFile,
        Func<IReadOnlyList<Guid>, CancellationToken, Task<bool>> stop,
        Func<IReadOnlyList<Guid>, CancellationToken, Task> start,
        Func<Guid, IReadOnlyList<string>, CancellationToken, Task> write,
        Func<Guid, Func<CancellationToken, Task>, CancellationToken, Task>? withStoppedApply = null,
        Func<ServerDefinition, IReadOnlyList<string>, CancellationToken, Task>? removeFiles = null,
        Func<Guid, Func<CancellationToken, Task>, CancellationToken, Task>? withRunningQueue = null,
        bool supportsAsaRemoval = false)
    {
        this.repository = repository; pending = new(pendingFile);
        gate = Gates.GetOrAdd(Path.GetFullPath(pendingFile), _ => new(1, 1));
        this.stop = stop; this.start = start; this.write = write;
        this.withStoppedApply = withStoppedApply ?? ((_, change, ct) => change(ct));
        this.withRunningQueue = withRunningQueue ?? ((_, change, ct) => change(ct));
        this.removeFiles = removeFiles;
        this.supportsAsaRemoval = supportsAsaRemoval;
    }

    public async Task<SettingsSaveResult> SaveAsync(Guid id, IReadOnlyList<string> order, bool running,
        SaveChoice choice, CancellationToken ct = default, bool replace = false)
    {
        if (!Enum.IsDefined(choice)) throw new ArgumentException("Choose a valid save action.");
        if (choice == SaveChoice.Cancel) return new(false, false, false, "Changes canceled.");
        var copied = order.ToList();
        var document = await repository.ReadAsync(ct);
        var selected = document.Servers.Single(s => s.Id == id);
        var validation = new ServerCatalog();
        foreach (var cluster in document.Clusters) validation.AddCluster(cluster);
        foreach (var server in document.Servers) validation.AddServer(server);
        if (replace)
        {
            if (!Path.IsPathFullyQualified(selected.Directory) || removeFiles is null)
                throw new NotSupportedException("This installation's mod replacement and file cleanup are not configured.");
            AsaModLaunchArguments.Build(selected.Mods); validation.ReplaceMods(id, copied);
            if (!supportsAsaRemoval && selected.Edition == ArkEdition.ASA && selected.Mods.Select(ulong.Parse).Except(copied.Select(ulong.Parse)).Any())
                throw new NotSupportedException("ASA mod removal needs its verified native cache cleanup adapter.");
        }
        else validation.SetModOrder(id, copied);
        await gate.WaitAsync(ct);
        try
        {
            var prior = (await ReadPendingAsync(ct)).Changes.SingleOrDefault(c => c.ServerId == id);
            if (prior is { Replace: true } && !selected.Mods.SequenceEqual(prior.PreviousOrder!))
                throw new InvalidOperationException("Previous mod changes still need cleanup. Restart the selected map to retry them before saving another change.");
        }
        finally { gate.Release(); }
        IReadOnlyList<Guid> targets = [id];
        // Lifecycle callbacks re-enter ApplyBeforeStartAsync; never hold this lock during stop/start.
        if (running && choice == SaveChoice.SaveAndRestart && !await stop(targets, ct))
            return new(false, false, false, "The server did not stop; mod order was not applied.");
        if (running && choice == SaveChoice.SaveForNextStart) await withRunningQueue(id, SaveAndApplyAsync, ct);
        else await withStoppedApply(id, SaveAndApplyAsync, ct);
        if (running && choice == SaveChoice.SaveForNextStart)
            return new(true, true, false, "Mod order saved for the next start.");
        if (running) await start(targets, ct);
        return new(true, false, running, running ? "Mod order saved and restart requested." : "Mod order saved.");

        async Task SaveAndApplyAsync(CancellationToken token)
        {
            await gate.WaitAsync(token);
            try
            {
                var current = (await repository.ReadAsync(token)).Servers.Single(s => s.Id == id);
                if (current.Directory != selected.Directory || current.Edition != selected.Edition || current.ClusterId != selected.ClusterId
                    || !current.Mods.SequenceEqual(selected.Mods)) throw new InvalidOperationException("The reviewed mod list changed. Refresh before saving.");
                var changes = await ReadPendingAsync(token);
                var change = new PendingModOrder(id, copied) { Replace = replace,
                    SourceDirectory = replace ? selected.Directory : null, PreviousOrder = replace ? [.. selected.Mods] : null,
                    SourceEdition = replace ? selected.Edition : null };
                await pending.SaveAsync(changes with { Changes = [.. changes.Changes.Where(c => c.ServerId != id), change] }, token);
                if (!running || choice == SaveChoice.SaveAndRestart) await ApplyAsync(change, token);
            }
            finally { gate.Release(); }
        }
    }

    public async Task ApplyBeforeStartAsync(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var change = (await ReadPendingAsync(ct)).Changes.SingleOrDefault(c => c.ServerId == id);
            if (change is not null) await ApplyAsync(change, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<ModOrderPending> ReadAsync(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var change = (await ReadPendingAsync(ct)).Changes.SingleOrDefault(c => c.ServerId == id);
            return new(id, change?.Order.ToList(), change?.Replace ?? false);
        }
        finally { gate.Release(); }
    }

    private async Task ApplyAsync(PendingModOrder change, CancellationToken ct)
    {
        var id = change.ServerId; var order = change.Order;
        var document = await repository.ReadAsync(ct);
        var selected = document.Servers.Single(s => s.Id == id);
        var validation = new ServerCatalog();
        foreach (var cluster in document.Clusters) validation.AddCluster(cluster);
        foreach (var server in document.Servers) validation.AddServer(server);
        if (change.Replace)
        {
            if (selected.Edition != (change.SourceEdition ?? ArkEdition.ASE) || selected.Directory != change.SourceDirectory || removeFiles is null
                || !selected.Mods.SequenceEqual(change.PreviousOrder!) && !selected.Mods.SequenceEqual(order))
                throw new InvalidOperationException("The installation changed before mod replacement. Pending changes were retained.");
            validation.ReplaceMods(id, order);
            if (!supportsAsaRemoval && selected.Edition == ArkEdition.ASA && change.PreviousOrder!.Select(ulong.Parse).Except(order.Select(ulong.Parse)).Any())
                throw new NotSupportedException("ASA mod removal needs its verified native cache cleanup adapter.");
        }
        else validation.SetModOrder(id, order);
        await write(id, order.ToArray(), ct);
        await repository.ChangeAsync(catalog =>
        {
            var current = catalog.Servers.Single(s => s.Id == id);
            if (current.Directory != selected.Directory || current.Edition != selected.Edition || current.ClusterId != selected.ClusterId
                || !current.Mods.SequenceEqual(selected.Mods)) throw new InvalidOperationException("The server changed during mod configuration. Pending order was retained.");
            if (change.Replace) catalog.ReplaceMods(id, order); else catalog.SetModOrder(id, order);
        }, ct);
        if (change.Replace)
        {
            var wanted = order.Select(id => ulong.Parse(id)).ToHashSet();
            var removed = change.PreviousOrder!.Where(id => !wanted.Contains(ulong.Parse(id))).ToArray();
            if (removed.Length > 0)
            {
                await removeFiles!(selected, removed, ct);
            }
        }
        var changes = await ReadPendingAsync(ct);
        await pending.SaveAsync(changes with { Changes = changes.Changes.Where(c => c.ServerId != id).ToList() }, ct);
    }

    private async Task<PendingModOrders> ReadPendingAsync(CancellationToken ct)
    {
        var document = await pending.LoadAsync(ct) ?? new(1, []);
        if (document.SchemaVersion != 1 || document.Changes is null
            || document.Changes.Any(c => c is null || c.ServerId == Guid.Empty || c.Order is null
                || c.Order.Any(string.IsNullOrWhiteSpace) || c.Order.Distinct().Count() != c.Order.Count)
            || document.Changes.Select(c => c.ServerId).Distinct().Count() != document.Changes.Count)
            throw new InvalidDataException("Invalid pending mod order document.");
        foreach (var change in document.Changes.Where(c => c.Replace))
        {
            if (change.SourceDirectory is null || !Path.IsPathFullyQualified(change.SourceDirectory) || change.PreviousOrder is null)
                throw new InvalidDataException("Invalid pending mod replacement.");
            if (change.SourceEdition is ArkEdition edition && !Enum.IsDefined(edition)) throw new InvalidDataException("Invalid pending mod edition.");
            try { AsaModLaunchArguments.Build(change.Order); AsaModLaunchArguments.Build(change.PreviousOrder); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid pending mod replacement IDs.", error); }
        }
        return document;
    }
}
