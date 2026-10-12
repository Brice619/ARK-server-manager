namespace ArkServerManager.Core;

public sealed record ServerDeletionPlan(Guid ServerId, string ServerName, string ServerDirectory,
    Guid? PreservedClusterId, IReadOnlyList<string> ProtectedSharedPaths)
{
    /// <summary>Read-only scope validation. Execution still requires stopping, confirmation and fresh filesystem checks.</summary>
    public static ServerDeletionPlan Create(CatalogDocument catalog, Guid serverId, string managementRoot,
        IReadOnlyList<string> sharedClusterPaths)
    {
        var server = catalog.Servers.SingleOrDefault(s => s.Id == serverId)
            ?? throw new ArgumentException("Choose an existing server.");
        if (server.ClusterId is not null && sharedClusterPaths.Count == 0)
            throw new InvalidOperationException("Identify shared cluster data and backup locations before deleting this map.");
        var target = Normalize(server.Directory);
        var root = Normalize(managementRoot);
        if (Contains(target, root)) throw new InvalidOperationException("Deleting this server would include the management folder.");
        foreach (var other in catalog.Servers.Where(s => s.Id != serverId))
        {
            var otherPath = Normalize(other.Directory);
            if (Contains(target, otherPath) || Contains(otherPath, target))
                throw new InvalidOperationException("This installation overlaps another server. Its files cannot be deleted as one folder.");
        }
        var shared = sharedClusterPaths.Select(Normalize).Distinct(PathComparer).ToArray();
        if (shared.Any(path => Contains(target, path) || Contains(path, target)))
            throw new InvalidOperationException("Shared cluster files overlap this folder. A separate file inventory is required.");
        return new(server.Id, server.Settings.Name, target, server.ClusterId, shared);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Deletion scope requires absolute folder paths.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (PathComparer.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!)))
            throw new InvalidOperationException("A filesystem root cannot be a deletion target.");
        return full;
    }
    private static bool Contains(string parent, string child) => PathComparer.Equals(parent, child)
        || child.StartsWith(parent + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
