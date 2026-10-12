using System.Collections.Concurrent;

namespace ArkServerManager.Core;

public sealed record CatalogDocument(int SchemaVersion, List<ClusterDefinition> Clusters, List<ServerDefinition> Servers);

/// <summary>Reloads and validates the catalog for each serialized transaction before publishing a complete document.</summary>
public sealed class CatalogRepository
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string path;
    private readonly AtomicJsonStore<CatalogDocument> store;
    private readonly SemaphoreSlim gate;

    public CatalogRepository(string path)
    {
        this.path = Path.GetFullPath(path);
        store = new(this.path, privateFile: true);
        gate = Gates.GetOrAdd(this.path, _ => new(1, 1));
    }

    public async Task<CatalogDocument> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return ToDocument(await LoadCatalogAsync(cancellationToken)); }
        finally { gate.Release(); }
    }

    public Task<CatalogDocument> ChangeAsync(Action<ServerCatalog> change, CancellationToken cancellationToken = default)
        => ChangeAsync((catalog, _) => { change(catalog); return Task.CompletedTask; }, cancellationToken);

    public async Task<CatalogDocument> ChangeAsync(Func<ServerCatalog, CancellationToken, Task> change, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // All writers of this repository must honor this lock; the background host will own it.
            await using var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var catalog = await LoadCatalogAsync(cancellationToken);
            await change(catalog, cancellationToken);
            var document = ToDocument(catalog);
            await store.SaveAsync(document, cancellationToken);
            return document;
        }
        finally { gate.Release(); }
    }

    private async Task<ServerCatalog> LoadCatalogAsync(CancellationToken ct)
    {
        var document = await store.LoadAsync(ct) ?? new(1, [], []);
        if (document.SchemaVersion != 1) throw new InvalidDataException("Unsupported catalog version.");
        if (document.Clusters is null || document.Servers is null) throw new InvalidDataException("Catalog collections are missing.");
        var catalog = new ServerCatalog();
        try
        {
            foreach (var cluster in document.Clusters) catalog.AddCluster(cluster);
            foreach (var server in document.Servers) catalog.AddServer(server);
        }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or NullReferenceException)
        { throw new InvalidDataException("The catalog contains invalid server or cluster data.", error); }
        return catalog;
    }

    private static CatalogDocument ToDocument(ServerCatalog catalog) => new(1, [.. catalog.Clusters], [.. catalog.Servers]);
}
