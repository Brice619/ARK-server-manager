using System.Collections.Concurrent;

namespace ArkServerManager.Core;

public enum SaveChoice { Cancel, SaveForNextStart, SaveAndRestart }
public sealed record PendingSettings(Guid ServerId, ServerSettings Settings, string? SourceSHA256 = null);
public sealed record PendingSettingsDocument(int SchemaVersion, List<PendingSettings> Changes);
public sealed record SettingsSaveResult(bool Saved, bool Pending, bool Restarted, string Message);

/// <summary>Applies the user's save choice through explicit process and configuration callbacks.</summary>
public sealed class SettingsChangeWorkflow
{
    private readonly CatalogRepository repository;
    private readonly AtomicJsonStore<PendingSettingsDocument> pendingStore;
    private readonly Func<IReadOnlyList<Guid>, CancellationToken, Task<bool>> stop;
    private readonly Func<IReadOnlyList<Guid>, CancellationToken, Task> start;
    private readonly Func<Guid, ServerSettings, CancellationToken, Task> writeConfiguration;
    private readonly Func<Guid, ServerSettings, string?, CancellationToken, Task>? writeReviewedConfiguration;
    private readonly Func<Guid, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedApply;
    private readonly Func<Guid, Func<CancellationToken, Task>, CancellationToken, Task> withRunningQueue;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim gate;

    public async Task RemoveDeletedAsync(ServerDeletionRecord deletion, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            DeletedServerRecordCleanup.RequireCompleted(await repository.ReadAsync(ct), deletion);
            var document = await LoadPendingAsync(ct);
            if (!document.Changes.Any(p => p.ServerId == deletion.ServerId)) return;
            await pendingStore.SaveAsync(document with { Changes = document.Changes.Where(p => p.ServerId != deletion.ServerId).ToList() }, ct);
        }
        finally { gate.Release(); }
    }

    public SettingsChangeWorkflow(CatalogRepository repository, string pendingFile,
        Func<IReadOnlyList<Guid>, CancellationToken, Task<bool>> stop,
        Func<IReadOnlyList<Guid>, CancellationToken, Task> start,
        Func<Guid, ServerSettings, CancellationToken, Task> writeConfiguration,
        Func<Guid, Func<CancellationToken, Task>, CancellationToken, Task>? withStoppedApply = null,
        Func<Guid, ServerSettings, string?, CancellationToken, Task>? writeReviewedConfiguration = null,
        Func<Guid, Func<CancellationToken, Task>, CancellationToken, Task>? withRunningQueue = null)
    {
        this.repository = repository;
        pendingStore = new(pendingFile);
        gate = Gates.GetOrAdd(Path.GetFullPath(pendingFile), _ => new(1, 1));
        this.stop = stop; this.start = start; this.writeConfiguration = writeConfiguration;
        this.withStoppedApply = withStoppedApply ?? ((_, change, ct) => change(ct));
        this.withRunningQueue = withRunningQueue ?? ((_, change, ct) => change(ct));
        this.writeReviewedConfiguration = writeReviewedConfiguration;
    }

    public async Task<SettingsSaveResult> SaveAsync(Guid id, ServerSettings settings, bool running, SaveChoice choice,
        CancellationToken ct = default, ServerDefinition? expectedServer = null, string? sourceSHA256 = null)
    {
        if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
        if (choice == SaveChoice.Cancel) return new(false, false, false, "Changes canceled.");
        if (sourceSHA256 is not null && (sourceSHA256.Length != 64 || sourceSHA256.Any(c => !char.IsAsciiHexDigit(c))))
            throw new ArgumentException("Load the configuration before saving.");
        if (sourceSHA256 is not null && writeReviewedConfiguration is null)
            throw new NotSupportedException("Reviewed configuration writing is not configured.");
        settings = settings.Copy();
        var reviewed = await repository.ReadAsync(ct);
        var selected = reviewed.Servers.Single(s => s.Id == id);
        if (expectedServer is not null && (expectedServer.Id != id || expectedServer.Directory != selected.Directory
            || expectedServer.Edition != selected.Edition || expectedServer.ClusterId != selected.ClusterId
            || expectedServer.Map != selected.Map || !SameSettings(expectedServer.Settings, selected.Settings)))
            throw new InvalidOperationException("The reviewed server changed. Reload its settings before saving.");
        var preview = new ServerCatalog();
        foreach (var cluster in reviewed.Clusters) preview.AddCluster(cluster);
        foreach (var server in reviewed.Servers) preview.AddServer(server);
        preview.UpdateSettings(id, settings);
        IReadOnlyList<Guid> targets = [id];
        // Lifecycle callbacks can re-enter ApplyBeforeStartAsync. Never hold the pending
        // settings lock while waiting on either stop or start.
        if (running && choice == SaveChoice.SaveAndRestart && !await stop(targets, ct))
            return new(false, false, false, "The server did not stop; settings were not applied.");
        SettingsSaveResult? saved = null;
        if (running && choice == SaveChoice.SaveForNextStart)
            await withRunningQueue(id, ApplyReviewedAsync, ct);
        else
            await withStoppedApply(id, ApplyReviewedAsync, ct);
        if (saved is not null) return saved;
        await start(targets, ct);
        return new(true, false, true, "Settings saved and restart requested.");

        async Task ApplyReviewedAsync(CancellationToken token)
        {
          await gate.WaitAsync(token);
          try
          {
            var document = await repository.ReadAsync(token);
            var current = document.Servers.SingleOrDefault(s => s.Id == id);
            if (current is null || current.Directory != selected.Directory || current.Edition != selected.Edition
                || current.ClusterId != selected.ClusterId || current.Map != selected.Map
                || !SameSettings(current.Settings, selected.Settings))
                throw new InvalidOperationException("The reviewed server changed. Review its settings before saving.");
            var validation = new ServerCatalog();
            foreach (var cluster in document.Clusters) validation.AddCluster(cluster);
            foreach (var server in document.Servers) validation.AddServer(server);
            validation.UpdateSettings(id, settings);
            if (running && choice == SaveChoice.SaveForNextStart)
            {
                await SetPendingAsync(id, settings, sourceSHA256, token);
                saved = new(true, true, false, "Settings saved for the next start.");
                return;
            }
            // Keep a durable pending copy until all configuration/catalog effects succeed.
            await SetPendingAsync(id, settings, sourceSHA256, token);
            await ApplyAsync(id, settings, sourceSHA256, token);
            if (!running) saved = new(true, false, false, "Settings saved.");
          }
          finally { gate.Release(); }
        }
    }

    /// <summary>Returns a detached queued-settings snapshot without applying it.</summary>
    public async Task<PendingSettings?> ReadPendingAsync(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var change = (await LoadPendingAsync(ct)).Changes.SingleOrDefault(c => c.ServerId == id);
            return change is null ? null : change with { Settings = change.Settings.Copy() };
        }
        finally { gate.Release(); }
    }

    /// <summary>The caller must ensure the server is stopped and serialize this with its next startup.</summary>
    public async Task ApplyBeforeStartAsync(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var pending = await LoadPendingAsync(ct);
            var change = pending.Changes.SingleOrDefault(c => c.ServerId == id);
            if (change is not null) await ApplyAsync(id, change.Settings, change.SourceSHA256, ct);
        }
        finally { gate.Release(); }
    }

    private async Task ApplyAsync(Guid id, ServerSettings settings, string? sourceSHA256, CancellationToken ct)
    {
        var original = await repository.ReadAsync(ct);
        var selected = original.Servers.SingleOrDefault(s => s.Id == id)
            ?? throw new InvalidOperationException("The server was removed before settings could be applied.");
        var validation = new ServerCatalog();
        foreach (var cluster in original.Clusters) validation.AddCluster(cluster);
        foreach (var server in original.Servers) validation.AddServer(server);
        validation.UpdateSettings(id, settings);
        // The configuration adapter may itself read the catalog. Do not invoke it under
        // the non-reentrant repository lock. The durable pending entry survives either failure.
        if (writeReviewedConfiguration is not null) await writeReviewedConfiguration(id, settings.Copy(), sourceSHA256, ct);
        else if (sourceSHA256 is null) await writeConfiguration(id, settings.Copy(), ct);
        else throw new NotSupportedException("Reviewed configuration writing is not configured.");
        await repository.ChangeAsync(catalog =>
        {
            var current = catalog.Servers.SingleOrDefault(s => s.Id == id);
            if (current is null || current.Directory != selected.Directory || current.Edition != selected.Edition
                || current.ClusterId != selected.ClusterId || current.Map != selected.Map
                || !SameSettings(current.Settings, selected.Settings))
                throw new InvalidOperationException("The server changed while applying settings. Pending settings were retained for review.");
            catalog.UpdateSettings(id, settings);
        }, ct);
        var pending = await LoadPendingAsync(ct);
        await pendingStore.SaveAsync(pending with { Changes = pending.Changes.Where(c => c.ServerId != id).ToList() }, ct);
    }

    private static bool SameSettings(ServerSettings first, ServerSettings second)
        => first.Name == second.Name && first.MaxPlayers == second.MaxPlayers && first.GamePort == second.GamePort
            && first.QueryPort == second.QueryPort && first.RconPort == second.RconPort
            && first.Values.Count == second.Values.Count
            && first.Values.All(pair => second.Values.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private async Task SetPendingAsync(Guid id, ServerSettings settings, string? sourceSHA256, CancellationToken ct)
    {
        var pending = await LoadPendingAsync(ct);
        await pendingStore.SaveAsync(pending with { Changes = [.. pending.Changes.Where(c => c.ServerId != id), new(id, settings.Copy(), sourceSHA256)] }, ct);
    }

    private async Task<PendingSettingsDocument> LoadPendingAsync(CancellationToken ct)
    {
        var pending = await pendingStore.LoadAsync(ct) ?? new(1, []);
        if (pending.SchemaVersion != 1 || pending.Changes is null
            || pending.Changes.Any(c => c is null || c.ServerId == Guid.Empty || c.Settings is null || c.Settings.Values is null)
            || pending.Changes.GroupBy(c => c.ServerId).Any(g => g.Count() > 1))
            throw new InvalidDataException("Invalid pending settings document.");
        foreach (var change in pending.Changes)
        {
            if (change.SourceSHA256 is string hash && (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c))))
                throw new InvalidDataException("Invalid pending configuration hash.");
            try { change.Settings.Validate(); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid pending server settings.", error); }
        }
        return pending;
    }
}
