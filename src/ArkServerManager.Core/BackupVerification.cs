namespace ArkServerManager.Core;

public sealed record BackupVerificationRequest(Guid ServerId, long RuntimeHour);
public sealed record SharedBackupVerificationRequest(Guid ClusterId, long RuntimeHour);
public sealed record SharedBackupVerificationResult(Guid ClusterId, long RuntimeHour, int FileCount,
    long TotalBytes, DateTimeOffset VerifiedAt, SnapshotCapture? Capture);
public sealed record BackupVerificationResult(Guid ServerId, long RuntimeHour, int FileCount,
    long TotalBytes, DateTimeOffset VerifiedAt)
{
    public SnapshotLayout Layout { get; init; }
}

public static class BackupVerification
{
    public static async Task<SharedBackupVerificationResult> VerifySharedAsync(CatalogRepository repository,
        string managementRoot, SharedBackupVerificationRequest request, CancellationToken ct = default)
    {
        var catalog = await repository.ReadAsync(ct);
        var cluster = catalog.Clusters.SingleOrDefault(c => c.Id == request.ClusterId)
            ?? throw new ArgumentException("Choose a registered cluster.");
        var storage = ManagedClusterStorage.Resolve(catalog, cluster.Id, managementRoot);
        var history = await AutomaticBackupService.ReadRecordedClusterAsync(catalog, cluster.Id, managementRoot, ct);
        var point = history.History.SingleOrDefault(h => h.RuntimeHour == request.RuntimeHour)
            ?? throw new ArgumentException("This shared restore point is no longer recorded.");
        var path = Path.Combine(storage.Backups, point.FileName);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The shared backup archive must not be a link.");
        var manifest = await new SnapshotArchive().VerifyAsync(path, ct);
        if (manifest.ServerId != cluster.Id || manifest.Edition != cluster.Edition
            || manifest.RuntimeHour != point.RuntimeHour || manifest.Scope != SnapshotScope.SharedCluster)
            throw new InvalidDataException("The archive belongs to another cluster, edition, scope or restore point.");
        return new(cluster.Id, point.RuntimeHour, manifest.Files.Count, manifest.Files.Sum(f => f.Length),
            DateTimeOffset.UtcNow, manifest.Capture);
    }

    public static async Task<BackupVerificationResult> VerifyAsync(CatalogRepository repository,
        BackupVerificationRequest request, CancellationToken ct = default)
    {
        var server = (await repository.ReadAsync(ct)).Servers.SingleOrDefault(s => s.Id == request.ServerId)
            ?? throw new ArgumentException("Choose a registered server.");
        var history = await AutomaticBackupService.ReadRecordedAsync(server, ct);
        var point = history.History.SingleOrDefault(h => h.RuntimeHour == request.RuntimeHour)
            ?? throw new ArgumentException("This restore point is no longer recorded.");
        var path = Path.GetFullPath(Path.Combine(server.Directory, "backups", point.FileName));
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked backup locations require separate validation.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The backup archive must not be a link.");
        var manifest = await new SnapshotArchive().VerifyAsync(path, ct);
        if (manifest.ServerId != server.Id || manifest.Edition != server.Edition || manifest.RuntimeHour != point.RuntimeHour
            || manifest.Scope != SnapshotScope.Server)
            throw new InvalidDataException("The archive belongs to another server, edition or restore point.");
        return new(server.Id, point.RuntimeHour, manifest.Files.Count, manifest.Files.Sum(f => f.Length), DateTimeOffset.UtcNow)
            { Layout = manifest.Layout };
    }
}
