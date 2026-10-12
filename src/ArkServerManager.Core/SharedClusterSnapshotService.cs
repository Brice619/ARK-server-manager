using System.Collections.Concurrent;

namespace ArkServerManager.Core;

/// <summary>Copies shared transfer state only while the caller holds a coordinated stable-state lease.</summary>
public sealed class SharedClusterSnapshotService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SnapshotArchive archive = new();

    public async Task<SnapshotManifest> CreateAsync(CatalogDocument catalog, Guid clusterId,
        string managementRoot, long runtimeHour,
        Func<ClusterStorageLocations, CancellationToken, Task<IStableServerState>> acquireStableState,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(acquireStableState);
        if (runtimeHour < 1) throw new ArgumentOutOfRangeException(nameof(runtimeHour));
        var locations = ManagedClusterStorage.Resolve(catalog, clusterId, managementRoot);
        var edition = catalog.Clusters.Single(c => c.Id == clusterId).Edition;
        var gate = Gates.GetOrAdd(locations.Backups, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            locations = ManagedClusterStorage.Prepare(catalog, clusterId, managementRoot);
            // The lease must coordinate transfers across all members, not merely save one map.
            await using var stable = await acquireStableState(locations, ct);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(stable.SourceDirectory)),
                Path.TrimEndingDirectorySeparator(locations.Transfers), comparison))
                throw new InvalidOperationException("The shared backup lease must protect this cluster's transfer folder.");
            ManagedClusterStorage.Resolve(catalog, clusterId, managementRoot);
            var target = Path.Combine(locations.Backups, $"hour-{runtimeHour:D8}.zip");
            // Archive publication never replaces an existing restore point.
            return await archive.CreateAsync(stable.Directory, target, clusterId, edition, runtimeHour, ct,
                SnapshotScope.SharedCluster, capture: stable.Capture);
        }
        finally { gate.Release(); }
    }
}
