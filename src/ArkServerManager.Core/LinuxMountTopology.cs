namespace ArkServerManager.Core;

public sealed record LinuxMount(int Id, int ParentId, string Device, string Root, string Directory,
    string FileSystem, string Source);

/// <summary>Resolves the actual containing mount, including bind mounts and nested filesystems.</summary>
public static class LinuxMountTopology
{
    public static IReadOnlyList<LinuxMount> Parse(string text)
    {
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("Linux mount information is too large.");
        var mounts = new List<LinuxMount>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var separator = Array.IndexOf(fields, "-");
            if (separator < 6 || fields.Length < separator + 4 || !int.TryParse(fields[0], out var id) || id <= 0
                || !int.TryParse(fields[1], out var parent) || parent < 0 || mounts.Any(m => m.Id == id))
                throw new InvalidDataException("Invalid Linux mount information.");
            var root = Decode(fields[3]); var directory = Decode(fields[4]);
            ValidatePath(root); ValidatePath(directory);
            mounts.Add(new(id, parent, fields[2], root, directory, fields[separator + 1], Decode(fields[separator + 2])));
        }
        if (mounts.Count == 0) throw new InvalidDataException("Linux mount information is empty.");
        return mounts;
    }

    public static LinuxMount Resolve(string path, IReadOnlyList<LinuxMount> mounts)
    {
        ValidatePath(path);
        var candidates = mounts.Where(m => Contains(m.Directory, path)).OrderByDescending(m => m.Directory.Length).ToArray();
        if (candidates.Length == 0 || candidates.Length > 1 && candidates[0].Directory == candidates[1].Directory)
            throw new InvalidDataException("The Linux storage mount could not be resolved uniquely.");
        return candidates[0];
    }

    public static LinuxMount RequireSingleBtrfsMount(IReadOnlyList<string> sources, string snapshotStorage,
        IReadOnlyList<LinuxMount> mounts)
    {
        if (sources.Count == 0 || sources.Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw new ArgumentException("Choose distinct saved-state sources.");
        var owner = Resolve(snapshotStorage, mounts);
        if (owner.FileSystem != "btrfs") throw new NotSupportedException("This capture method requires Btrfs snapshot storage.");
        foreach (var source in sources)
        {
            var selected = Resolve(source, mounts);
            if (selected.Id != owner.Id)
                throw new NotSupportedException("These saved-state directories require a capture across separate Linux mounts.");
            if (mounts.Any(m => m.Id != owner.Id && Contains(source, m.Directory)))
                throw new NotSupportedException("A saved-state directory contains another mounted filesystem; capture it explicitly.");
            if (Contains(source, snapshotStorage) || Contains(snapshotStorage, source))
                throw new ArgumentException("Snapshot storage must be separate from saved-state sources.");
        }
        return owner;
    }

    public static bool Contains(string parent, string path)
        => path == parent || parent == "/" && path.StartsWith('/') || path.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/') || path.IndexOf('\0') >= 0
            || path.Contains("//", StringComparison.Ordinal) || path.Split('/').Any(part => part is "." or "..")
            || path.Length > 1 && path.EndsWith('/'))
            throw new InvalidDataException("Choose a normalized absolute Linux path.");
    }

    private static string Decode(string value)
    {
        var result = new System.Text.StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\') { result.Append(value[index]); continue; }
            if (index + 3 >= value.Length) throw new InvalidDataException("Invalid Linux mount path escape.");
            var escape = value.Substring(index + 1, 3);
            result.Append(escape switch { "040" => ' ', "011" => '\t', "012" => '\n', "134" => '\\',
                _ => throw new InvalidDataException("Unexpected Linux mount path escape.") });
            index += 3;
        }
        return result.ToString();
    }
}
