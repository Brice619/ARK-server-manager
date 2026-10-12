namespace ArkServerManager.Core;

public sealed record ClusterStorageLocations(Guid ClusterId, string Directory, string Transfers, string Backups)
{
    public IReadOnlyList<string> ProtectedPaths => [Directory];
}

/// <summary>Shared cluster state is kept outside every map's installation and deletion scope.</summary>
public static class ManagedClusterStorage
{
    public static ClusterStorageLocations Resolve(CatalogDocument catalog, Guid clusterId, string managementRoot)
    {
        if (clusterId == Guid.Empty || !catalog.Clusters.Any(c => c.Id == clusterId))
            throw new ArgumentException("Choose a registered cluster.");
        if (!Path.IsPathFullyQualified(managementRoot)) throw new ArgumentException("Choose an absolute management folder.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managementRoot));
        if (root == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!))
            throw new ArgumentException("Choose a dedicated management folder.");
        var directory = Path.Combine(root, "Clusters", clusterId.ToString("N"));
        foreach (var server in catalog.Servers)
            if (Contains(directory, server.Directory) || Contains(server.Directory, directory))
                throw new InvalidOperationException("Shared cluster storage overlaps a server folder. Review its location.");
        var result = new ClusterStorageLocations(clusterId, directory, Path.Combine(directory, "Transfers"), Path.Combine(directory, "Backups"));
        foreach (var path in new[] { result.Directory, result.Transfers, result.Backups }) CheckPath(path);
        return result;
    }

    public static ClusterStorageLocations Prepare(CatalogDocument catalog, Guid clusterId, string managementRoot)
    {
        var locations = Resolve(catalog, clusterId, managementRoot);
        foreach (var path in new[] { locations.Directory, locations.Transfers, locations.Backups })
        {
            CheckPath(path); Directory.CreateDirectory(path); CheckPath(path);
        }
        return locations;
    }

    private static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current)) throw new IOException("Cluster storage requires directories, not files.");
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked cluster storage needs separate validation.");
        }
    }

    private static bool Contains(string parent, string child)
    {
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(parent, child, comparison) || child.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }
}
