namespace ArkServerManager.Core;
/// <summary>Revision must include filesystem identity and change-time, not only size or modification time.</summary>
public sealed record StableCaptureEntry(string RelativePath, bool IsDirectory, long Bytes, string Revision);
public static class StableCaptureInventory
{
    public static void RequireUnchanged(IReadOnlyList<StableCaptureEntry> before, IReadOnlyList<StableCaptureEntry> after)
    {
        var original = Validate(before); var current = Validate(after);
        if (original.Count != current.Count || original.Any(item => !current.TryGetValue(item.Key, out var entry) || entry != item.Value))
            throw new IOException("Saved state changed during capture. Discard this capture and retry without stopping the server.");
    }
    private static Dictionary<string, StableCaptureEntry> Validate(IReadOnlyList<StableCaptureEntry> inventory)
    {
        if (inventory is null || inventory.Count > 250000) throw new InvalidDataException("Unsupported capture inventory.");
        var result = new Dictionary<string, StableCaptureEntry>(StringComparer.Ordinal);
        foreach (var entry in inventory)
        {
            if (entry is null || entry.RelativePath is null || entry.RelativePath.Length > 4096 || entry.Revision is null
                || entry.Revision.Length is < 1 or > 512 || entry.Bytes < 0 || entry.IsDirectory && entry.Bytes != 0
                || entry.RelativePath.Length == 0 && !entry.IsDirectory || entry.RelativePath.Contains('\\') || entry.RelativePath.Contains(':')
                || entry.RelativePath.StartsWith('/') || entry.RelativePath.Length > 0 && entry.RelativePath.Split('/').Any(part => part is "" or "." or "..")
                || !result.TryAdd(entry.RelativePath, entry)) throw new InvalidDataException("Invalid capture inventory entry.");
        }
        return result;
    }
}
