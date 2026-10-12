using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ArkServerManager.Core;

public static class RemovedAsaModFiles
{
    /// <summary>The caller owns a verified stopped installation's lifecycle/file scope.</summary>
    public static async Task DeleteAsync(ServerDefinition server, IReadOnlyList<string> removed, CancellationToken ct)
    {
        if (server.Edition != ArkEdition.ASA || !Path.IsPathFullyQualified(server.Directory))
            throw new ArgumentException("Choose an ASA installation.");
        AsaModLaunchArguments.Build(removed);
        var ids = removed.Select(ulong.Parse).ToHashSet();
        if (ids.Count == 0) return;
        var root = Path.GetFullPath(server.Directory);
        var native = Path.Combine(root, "ShooterGame", "Binaries", "Win64", "ShooterGame");
        var mods = Path.Combine(native, "Mods", "83374");
        var temporaryMods = Path.Combine(mods, ".temp");
        var libraryPath = Path.Combine(native, "ModsUserData", "83374", "library.json");
        RejectAncestors(mods); RejectAncestors(temporaryMods); RejectAncestors(libraryPath);
        byte[]? original = null; JsonObject? library = null;
        var targets = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (File.Exists(libraryPath))
        {
            await using var input = new FileStream(libraryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 16 * 1024 * 1024) throw new InvalidDataException("The native mod library exceeds its size limit.");
            original = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(original, ct);
            var hasBom = original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
            library = JsonNode.Parse(hasBom ? original.AsSpan(3) : original.AsSpan()) as JsonObject
                ?? throw new InvalidDataException("The native mod library is invalid.");
            var entries = library["installedMods"] as JsonArray ?? throw new InvalidDataException("The native mod library schema is unsupported.");
            foreach (var entry in entries.ToArray())
            {
                if (entry is not JsonObject item || item["installedFile"] is not JsonObject file)
                    throw new InvalidDataException("The native mod library contains an unsupported entry.");
                var projectId = Number(file["modId"]);
                if (!ids.Contains(projectId)) continue;
                var fileId = Number(file["iD"]);
                if (Number(file["gameId"]) != 83374 || item["details"] is not JsonObject details
                    || Number(details["iD"]) != projectId || Number(details["gameId"]) != 83374
                    || item["unmanaged"] is not JsonValue unmanaged || !unmanaged.TryGetValue<bool>(out var unmanagedValue) || unmanagedValue)
                    throw new InvalidDataException("The selected mod library entry cannot be verified as native managed storage.");
                var expected = $"83374/{projectId}_{fileId}";
                if (item["pathOnDisk"] is not JsonValue path || !path.TryGetValue<string>(out var relative)
                    || relative.Replace('\\', '/') != expected)
                    throw new InvalidDataException("The selected mod's recorded folder does not match its project and file IDs.");
                if (item["downloadInfo"] is JsonObject download && download["tempFile"] is JsonValue temp
                    && temp.TryGetValue<string>(out var tempFile) && !string.IsNullOrEmpty(tempFile))
                    throw new InvalidOperationException("The selected mod has an unfinished native download. Review it before removing its cache.");
                targets.Add(Path.Combine(mods, $"{projectId}_{fileId}"));
                entries.Remove(entry);
            }
        }
        // Include old native versions and unpacking folders for these exact project IDs.
        foreach (var parent in new[] { mods, temporaryMods })
            if (Directory.Exists(parent))
                foreach (var path in Directory.EnumerateDirectories(parent))
                {
                    var name = Path.GetFileName(path).Split('_');
                    if (name.Length == 2 && name.All(part => part.Length > 0 && part.All(char.IsAsciiDigit))
                        && ulong.TryParse(name[0], out var project) && ids.Contains(project)
                        && ulong.TryParse(name[1], out var file) && project > 0 && file > 0)
                        targets.Add(path);
                }
        foreach (var target in targets)
        {
            RequireWithin(mods, target); RejectAncestors(target);
            if (File.Exists(target)) throw new IOException("A native mod folder is occupied by a file.");
            if (!Directory.Exists(target)) continue;
            var directories = new Stack<string>(); directories.Push(target);
            while (directories.TryPop(out var directory))
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested(); RejectLink(entry);
                    if (Directory.Exists(entry)) directories.Push(entry);
                }
        }
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested(); RequireWithin(mods, target); RejectAncestors(target);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
        if (original is null || library is null) return;
        RejectAncestors(libraryPath);
        var current = await File.ReadAllBytesAsync(libraryPath, ct);
        if (!SHA256.HashData(current).SequenceEqual(SHA256.HashData(original)))
            throw new InvalidOperationException("The native mod library changed during cleanup. Its current contents were preserved.");
        var replacement = Encoding.UTF8.GetBytes(library.ToJsonString());
        if (original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) replacement = [0xef, 0xbb, 0xbf, .. replacement];
        var originalMode = OperatingSystem.IsLinux() ? File.GetUnixFileMode(libraryPath) : (UnixFileMode?)null;
        var temporary = libraryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, BufferSize = 8192, Options = FileOptions.WriteThrough };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporary, options))
            {
                await output.WriteAsync(replacement, ct); await output.FlushAsync(ct); output.Flush(true);
            }
            RejectAncestors(libraryPath);
            current = await File.ReadAllBytesAsync(libraryPath, ct);
            if (!SHA256.HashData(current).SequenceEqual(SHA256.HashData(original)))
                throw new InvalidOperationException("The native mod library changed before publication. Its current contents were preserved.");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, originalMode!.Value);
            ct.ThrowIfCancellationRequested(); File.Replace(temporary, libraryPath, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static ulong Number(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<ulong>(out var number) && number > 0 ? number
            : throw new InvalidDataException("The native mod library has an invalid numeric identity.");
    private static void RequireWithin(string root, string target)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(target));
        if (relative == "." || relative == ".." || Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new IOException("The mod cleanup target is outside native mod storage.");
    }
    private static void RejectAncestors(string path)
    { for (var parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent)) RejectLink(parent); }
    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null
            || (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked native mod storage cannot be removed automatically.");
    }
}
