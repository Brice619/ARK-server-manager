namespace ArkServerManager.Core;

public static class RemovedAseModFiles
{
    /// <summary>The caller owns the selected installation's stopped lifecycle/file scope.</summary>
    public static Task DeleteAsync(ServerDefinition server, IReadOnlyList<string> removed, CancellationToken ct)
    {
        if (server.Edition != ArkEdition.ASE || !Path.IsPathFullyQualified(server.Directory)) throw new ArgumentException("Choose an ASE installation.");
        AsaModLaunchArguments.Build(removed);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.Directory));
        var targets = new List<string>();
        foreach (var original in removed)
        {
            var id = ulong.Parse(original).ToString(System.Globalization.CultureInfo.InvariantCulture);
            targets.Add(Path.Combine(root, "ShooterGame", "Content", "Mods", id));
            targets.Add(Path.Combine(root, "ShooterGame", "Content", "Mods", id + ".mod"));
            foreach (var platform in new[] { "Win64", "Linux" })
                foreach (var cache in new[] { "content", "downloads" })
                    targets.Add(Path.Combine(root, "Engine", "Binaries", "ThirdParty", "SteamCMD", platform,
                        "steamapps", "workshop", cache, "346110", id));
        }
        // Preflight every owned target before deleting any of them.
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, Path.GetFullPath(target));
            if (relative == "." || relative == ".." || Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new IOException("A mod cleanup target is outside its installation.");
            for (var parent = target; parent is not null; parent = Path.GetDirectoryName(parent)) RejectLink(parent);
            if (Directory.Exists(target))
            {
                var pending = new Stack<string>(); pending.Push(target);
                while (pending.TryPop(out var folder))
                    foreach (var item in Directory.EnumerateFileSystemEntries(folder))
                    {
                        ct.ThrowIfCancellationRequested(); RejectLink(item);
                        if (Directory.Exists(item)) pending.Push(item);
                    }
            }
        }
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            RejectLink(target);
            for (var parent = Path.GetDirectoryName(target); parent is not null; parent = Path.GetDirectoryName(parent)) RejectLink(parent);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            else if (File.Exists(target)) File.Delete(target);
        }
        return Task.CompletedTask;
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked mod storage cannot be removed automatically.");
    }
}
