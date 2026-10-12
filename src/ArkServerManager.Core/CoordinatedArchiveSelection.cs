namespace ArkServerManager.Core;

public sealed record CoordinatedArchiveRequest(Guid ClusterId, long RuntimeHour, Guid? ServerId);
public sealed record CoordinatedArchiveVerification(Guid ClusterId, Guid? ServerId, long RuntimeHour,
    SnapshotCapture Capture, int FileCount, long TotalBytes, DateTimeOffset VerifiedAt);
public sealed record SelectedCoordinatedArchive(string Path, CoordinatedRestorePoint Point, ServerDefinition? Server);

/// <summary>Archive paths come exclusively from registered locations and validated published indexes.</summary>
public static class CoordinatedArchiveSelection
{
    public static async Task<SelectedCoordinatedArchive> ResolveAsync(CatalogDocument catalog, string root,
        CoordinatedArchiveRequest request, CancellationToken ct = default)
    {
        var history = await CoordinatedBackupRecords.ReadAsync(catalog, request.ClusterId, root, ct);
        var point = history.Points.SingleOrDefault(p => p.RuntimeHour == request.RuntimeHour)
            ?? throw new ArgumentException("Choose a recorded coordinated restore point.");
        var fileName = $"hour-{point.RuntimeHour:D8}-{point.Capture.Id:N}.zip";
        ServerDefinition? server = null;
        string path;
        if (request.ServerId is Guid id)
        {
            if (!point.Maps.Contains(id)) throw new ArgumentException("This map was not captured in the selected point.");
            server = catalog.Servers.SingleOrDefault(s => s.Id == id && s.ClusterId == request.ClusterId && s.Edition == point.Edition)
                ?? throw new ArgumentException("This captured map is no longer registered in the cluster.");
            path = Path.Combine(server.Directory, "backups", "Coordinated", fileName);
        }
        else path = Path.Combine(ManagedClusterStorage.Resolve(catalog, request.ClusterId, root).Backups, "Coordinated", fileName);
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Coordinated restore archives must not use links.");
        return new(path, point, server);
    }

    public static async Task<CoordinatedArchiveVerification> VerifyAsync(CatalogDocument catalog, string root,
        CoordinatedArchiveRequest request, CancellationToken ct = default)
    {
        var selected = await ResolveAsync(catalog, root, request, ct);
        var manifest = await new SnapshotArchive().VerifyAsync(selected.Path, ct);
        var scope = selected.Server is null ? SnapshotScope.SharedCluster : SnapshotScope.Server;
        var layout = selected.Server is null ? SnapshotLayout.Unspecified : SnapshotLayout.WorldSavedFolders;
        if (manifest.ServerId != (selected.Server?.Id ?? selected.Point.ClusterId) || manifest.Edition != selected.Point.Edition
            || manifest.RuntimeHour != selected.Point.RuntimeHour || manifest.Capture != selected.Point.Capture
            || manifest.Scope != scope || manifest.Layout != layout)
            throw new InvalidDataException("The archive does not belong to the selected coordinated capture.");
        return new(request.ClusterId, request.ServerId, request.RuntimeHour, selected.Point.Capture,
            manifest.Files.Count, manifest.Files.Sum(f => f.Length), DateTimeOffset.UtcNow);
    }
}
