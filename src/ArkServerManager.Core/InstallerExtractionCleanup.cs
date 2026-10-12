namespace ArkServerManager.Core;
public static class InstallerExtractionCleanup
{
    public static void Remove(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var name = Path.GetFileName(full);
        const string prefix = "ArkServerManager-Setup-";
        if (!string.Equals(Path.GetDirectoryName(full), temporary, comparison) || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[prefix.Length..], "N", out var id) || id == Guid.Empty)
            throw new IOException("Installer extraction cleanup is limited to its generated temporary folder.");
        if (!Directory.Exists(full)) return;
        for (var ancestor = new DirectoryInfo(full); ancestor is not null; ancestor = ancestor.Parent)
            RejectLink(ancestor);
        var pending = new Stack<DirectoryInfo>(); pending.Push(new(full));
        while (pending.TryPop(out var folder))
            foreach (var entry in folder.EnumerateFileSystemInfos())
            {
                RejectLink(entry);
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        Directory.Delete(full, recursive: true);
    }
    private static void RejectLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked installer extraction paths require separate cleanup.");
    }
}
