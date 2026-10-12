namespace ArkServerManager.Core;

public sealed record ServerStartupPreferences(int SchemaVersion, List<Guid> Servers, List<Guid> Clusters)
{
    public static ServerStartupPreferences Empty => new(1, [], []);
    public void Validate()
    {
        if (SchemaVersion != 1 || Servers is null || Clusters is null || Servers.Count + Clusters.Count > 4096
            || Servers.Concat(Clusters).Any(id => id == Guid.Empty)
            || Servers.Distinct().Count() != Servers.Count || Clusters.Distinct().Count() != Clusters.Count)
            throw new InvalidDataException("The automatic startup selection is invalid.");
    }
    public Guid[] Resolve(CatalogDocument catalog)
    {
        Validate();
        // Deleted entries are inert. A standalone moved into a cluster needs a new cluster choice.
        var knownClusters = catalog.Clusters.Select(c => c.Id).ToHashSet();
        return catalog.Servers.Where(s => s.ClusterId is Guid cluster
            ? knownClusters.Contains(cluster) && Clusters.Contains(cluster)
            : Servers.Contains(s.Id)).Select(s => s.Id).Distinct().ToArray();
    }
}

public sealed record SaveServerStartupPreferences(ServerStartupPreferences Preferences, bool Confirmed);

public sealed class ServerStartupPreferenceStore(string root, CatalogRepository repository)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = Gates.GetOrAdd(Path.GetFullPath(root), _ => new(1, 1));
    private readonly AtomicJsonStore<ServerStartupPreferences> store = new(Path.Combine(root, "app-settings", "server-startup.json"), true);
    public async Task<ServerStartupPreferences> ReadAsync(CancellationToken ct = default)
    {
        var value = await store.LoadAsync(ct) ?? ServerStartupPreferences.Empty;
        value.Validate(); return value;
    }
    public async Task<ServerStartupPreferences> SaveAsync(SaveServerStartupPreferences request, CancellationToken ct = default)
    {
        if (!request.Confirmed) throw new ArgumentException("Confirm the automatic startup selection.");
        request.Preferences.Validate();
        await gate.WaitAsync(ct);
        try
        {
        var catalog = await repository.ReadAsync(ct);
        if (request.Preferences.Servers.Any(id => !catalog.Servers.Any(s => s.Id == id && s.ClusterId is null))
            || request.Preferences.Clusters.Any(id => !catalog.Clusters.Any(c => c.Id == id)))
            throw new ArgumentException("A selected server or cluster changed. Review the selection again.");
        await store.SaveAsync(request.Preferences, ct); return request.Preferences;
        }
        finally { gate.Release(); }
    }
    public async Task RemoveDeletedAsync(ServerDeletionRecord deletion, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var catalog = await repository.ReadAsync(ct);
            DeletedServerRecordCleanup.RequireCompleted(catalog, deletion);
            var current = await store.LoadAsync(ct);
            if (current is null) return;
            current.Validate();
            var remaining = current with
            {
                Servers = current.Servers.Where(id => id != deletion.ServerId).ToList(),
                Clusters = current.Clusters.Where(id => catalog.Clusters.Any(c => c.Id == id)).ToList()
            };
            if (!current.Servers.SequenceEqual(remaining.Servers) || !current.Clusters.SequenceEqual(remaining.Clusters))
                await store.SaveAsync(remaining, ct);
        }
        finally { gate.Release(); }
    }
}
