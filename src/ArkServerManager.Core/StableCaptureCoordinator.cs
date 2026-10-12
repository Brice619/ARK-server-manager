namespace ArkServerManager.Core;

public sealed record StableCaptureSource(string SourceDirectory, string DestinationDirectory, IReadOnlyList<string>? SelectedDirectories);

/// <summary>All source inventories bracket all copying, so independently copied maps cannot silently represent different intervals.</summary>
public static class StableCaptureCoordinator
{
    public static async Task<SnapshotCapture> CopyAsync(IReadOnlyList<StableCaptureSource> sources,
        Func<StableCaptureSource, CancellationToken, Task<IReadOnlyList<StableCaptureEntry>>> read,
        Func<StableCaptureSource, StableCaptureEntry, CancellationToken, Task> copy,
        CancellationToken ct = default)
    {
        if (sources.Count is < 1 or > 129 || sources.Select(s => s.SourceDirectory).Distinct(StringComparer.Ordinal).Count() != sources.Count
            || sources.Select(s => s.DestinationDirectory).Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw new ArgumentException("Choose distinct saved-state sources and capture destinations.");
        var inventories = new List<IReadOnlyList<StableCaptureEntry>>();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            var inventory = await read(source, ct);
            StableCaptureInventory.RequireUnchanged(inventory, inventory);
            if (!inventory.Any(entry => entry.RelativePath.Length == 0 && entry.IsDirectory))
                throw new InvalidDataException("Capture inventory must include its source directory identity.");
            inventories.Add(inventory);
        }
        // This point lies after every initial inventory and before any copying or final inventory.
        var capture = new SnapshotCapture(Guid.NewGuid(), DateTimeOffset.UtcNow);
        for (var index = 0; index < sources.Count; index++)
            foreach (var entry in inventories[index])
            {
                ct.ThrowIfCancellationRequested();
                await copy(sources[index], entry, ct);
            }
        for (var index = 0; index < sources.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            StableCaptureInventory.RequireUnchanged(inventories[index], await read(sources[index], ct));
        }
        return capture;
    }
}
