namespace ArkServerManager.Core;

public sealed record BtrfsSubvolume(Guid Uuid, Guid? ParentUuid, ulong Id, bool ReadOnly)
{
    public void RequireSnapshotIdentity(Guid source, Guid? expected)
    {
        if (!ReadOnly || ParentUuid != source || Uuid == source || expected is Guid identity && Uuid != identity)
            throw new InvalidDataException("The Btrfs snapshot identity or read-only state changed; preserve it for review.");
    }

    public static IReadOnlyList<string> ListedFileSystemPaths(string text)
    {
        if (text.Length > 1024 * 1024) throw new InvalidDataException("Btrfs subvolume list is too large.");
        var paths = new List<string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(" path ", StringComparison.Ordinal);
            if (!line.StartsWith("ID ", StringComparison.Ordinal) || separator < 0)
                throw new InvalidDataException("Unexpected Btrfs subvolume list format.");
            var path = line[(separator + 6)..].TrimEnd('\r');
            if (path.StartsWith("<FS_TREE>/", StringComparison.Ordinal)) path = path[10..];
            if (path.Length == 0 || path.StartsWith('/') || path.Split('/').Any(part => part is "." or ".." or ""))
                throw new InvalidDataException("Invalid Btrfs subvolume list path.");
            paths.Add("/" + path);
        }
        return paths;
    }

    public static BtrfsSubvolume Parse(string text)
    {
        if (text.Length > 1024 * 1024) throw new InvalidDataException("Btrfs metadata is too large.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            var key = line[..separator].Trim();
            if (key is not ("UUID" or "Parent UUID" or "Subvolume ID" or "Flags")) continue;
            if (!fields.TryAdd(key, line[(separator + 1)..].Trim()))
                throw new InvalidDataException("Repeated Btrfs identity metadata.");
        }
        if (!Guid.TryParse(fields.GetValueOrDefault("UUID"), out var uuid) || uuid == Guid.Empty
            || !ulong.TryParse(fields.GetValueOrDefault("Subvolume ID"), out var id) || id < 5
            || !fields.TryGetValue("Parent UUID", out var parent) || !fields.TryGetValue("Flags", out var flags))
            throw new InvalidDataException("Incomplete Btrfs identity metadata.");
        Guid? parentUuid = null;
        if (parent != "-")
        {
            if (!Guid.TryParse(parent, out var parsed) || parsed == Guid.Empty) throw new InvalidDataException("Invalid Btrfs parent identity.");
            parentUuid = parsed;
        }
        return new(uuid, parentUuid, id, flags.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Contains("readonly", StringComparer.Ordinal));
    }
}
