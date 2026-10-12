using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record CoordinatedBackupHistoryRequest(Guid ClusterId);
public sealed record CoordinatedBackupHistory(Guid ClusterId, ArkEdition Edition, long RuntimeTicks,
    List<CoordinatedRestorePoint> Points, string? LastError);

/// <summary>Reads published indexes only. Recorded metadata does not establish current ZIP integrity.</summary>
public static class CoordinatedBackupRecords
{
    public static async Task<CoordinatedBackupHistory> ReadAsync(CatalogDocument catalog, Guid clusterId, string root,
        CancellationToken ct = default)
    {
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        var edition = catalog.Clusters.Single(c => c.Id == clusterId).Edition;
        var folder = Path.Combine(storage.Backups, "Coordinated");
        CheckPath(folder);
        if (!Directory.Exists(folder)) return new(clusterId, edition, 0, [], null);
        var schedulePath = Path.Combine(folder, "schedule.json");
        CheckPath(schedulePath);
        var schedule = await ReadBoundedAsync<CoordinatedBackupSchedule>(schedulePath, ct);
        if (schedule is not null && (schedule.SchemaVersion != 1 || schedule.ClusterId != clusterId
            || schedule.Edition != edition || schedule.RuntimeTicks < 0))
            throw new InvalidDataException("Unexpected coordinated backup runtime record.");
        var points = new List<CoordinatedRestorePoint>();
        foreach (var path in Directory.EnumerateFiles(folder, "hour-*.json"))
        {
            ct.ThrowIfCancellationRequested(); CheckPath(path);
            var point = await ReadBoundedAsync<CoordinatedRestorePoint>(path, ct)
                ?? throw new InvalidDataException("A coordinated restore point disappeared.");
            if (point.SchemaVersion != 1 || point.ClusterId != clusterId || point.Edition != edition || point.RuntimeHour < 1
                || Path.GetFileName(path) != $"hour-{point.RuntimeHour:D8}.json"
                || point.Capture is null || point.Capture.Id == Guid.Empty || point.Capture.CapturedAt == default
                || point.Maps is null || point.Maps.Count == 0 || point.Maps.Any(id => id == Guid.Empty)
                || point.Maps.Distinct().Count() != point.Maps.Count)
                throw new InvalidDataException("Unexpected published coordinated restore point.");
            if (schedule is null || point.RuntimeHour > schedule.RuntimeTicks / TimeSpan.TicksPerHour)
                throw new InvalidDataException("The published restore point has no matching runtime checkpoint.");
            if (new RuntimeBackupClock(TimeSpan.FromTicks(schedule.RuntimeTicks)).ShouldRetain(point.RuntimeHour)) points.Add(point);
        }
        return new(clusterId, edition, schedule?.RuntimeTicks ?? 0, points.OrderByDescending(p => p.RuntimeHour).ToList(), schedule?.LastError);
    }

    internal static async Task<T?> ReadBoundedAsync<T>(string path, CancellationToken ct)
    {
        FileStream input;
        try { input = await AtomicRecordReader.OpenAsync(path, ct); }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
        await using var stream = input;
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Coordinated backup metadata exceeds its size limit.");
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct)
                ?? throw new InvalidDataException("Coordinated backup metadata is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("A coordinated backup record could not be read. The original file is preserved for review.", error);
        }
    }
    private static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Coordinated backup records must not use links.");
    }
}
