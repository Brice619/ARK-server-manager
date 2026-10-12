namespace ArkServerManager.Core;

public sealed record CoordinatedClusterRestoreRequest(Guid ClusterId, long RuntimeHour, Guid CaptureId, bool Confirmed);
public sealed record PreparedClusterRestore(CoordinatedRestorePoint Point, List<SelectedCoordinatedArchive> Maps,
    SelectedCoordinatedArchive Shared);

/// <summary>Caller holds the verified stopped-cluster scope. Preparation verifies all archives without modifying state.</summary>
public static class CoordinatedClusterRestoreSelection
{
    public static async Task<PreparedClusterRestore> PrepareAsync(CatalogDocument catalog, string root,
        CoordinatedClusterRestoreRequest request, CancellationToken ct = default)
    {
        if (!request.Confirmed || request.ClusterId == Guid.Empty || request.RuntimeHour < 1 || request.CaptureId == Guid.Empty)
            throw new ArgumentException("Confirm a recorded cluster restore point.");
        var shared = await CoordinatedArchiveSelection.ResolveAsync(catalog, root, new(request.ClusterId, request.RuntimeHour, null), ct);
        var point = shared.Point;
        if (point.Capture.Id != request.CaptureId) throw new InvalidDataException("The selected cluster capture changed.");
        var members = catalog.Servers.Where(s => s.ClusterId == request.ClusterId).OrderBy(s => s.Id).ToArray();
        if (!point.Maps.OrderBy(id => id).SequenceEqual(members.Select(s => s.Id)))
            throw new InvalidOperationException("This point has different cluster maps. Restore its available maps individually instead.");
        var selectedMaps = new List<SelectedCoordinatedArchive>();
        foreach (var member in members)
        {
            var selection = new CoordinatedArchiveRequest(request.ClusterId, request.RuntimeHour, member.Id);
            var verified = await CoordinatedArchiveSelection.VerifyAsync(catalog, root, selection, ct);
            if (verified.Capture != point.Capture) throw new InvalidDataException("Cluster archives have different captures.");
            selectedMaps.Add(await CoordinatedArchiveSelection.ResolveAsync(catalog, root, selection, ct));
        }
        var verifiedShared = await CoordinatedArchiveSelection.VerifyAsync(catalog, root, new(request.ClusterId, request.RuntimeHour, null), ct);
        if (verifiedShared.Capture != point.Capture) throw new InvalidDataException("Shared data has a different capture.");
        return new(point, selectedMaps, shared);
    }
}
