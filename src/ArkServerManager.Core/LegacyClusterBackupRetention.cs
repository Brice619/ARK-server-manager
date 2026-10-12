namespace ArkServerManager.Core;

public sealed record LegacyBackupRetentionState(int SchemaVersion, Guid ServerId, Guid ClusterId, ArkEdition Edition,
    long OriginalRuntimeTicks, long AdditionalRuntimeTicks, RunningTimeObservation? Observation);

/// <summary>Caller holds the member file/lifecycle scope. Ages only recorded legacy positions, never creates new backups.</summary>
public static class LegacyClusterBackupRetention
{
    public static async Task TickAsync(ServerDefinition server, RunningTimeObservation? observation, CancellationToken ct = default)
    {
        if (server.ClusterId is not Guid clusterId) throw new ArgumentException("Choose a cluster map.");
        if (observation is { } reading && (reading.HostSession == Guid.Empty || reading.CumulativeTicks < 0))
            throw new ArgumentException("Choose a valid measured runtime.");
        var folder = Path.Combine(server.Directory, "backups");
        var historyPath = Path.Combine(folder, "backup-history.json");
        var clockPath = Path.Combine(folder, "legacy-retention.json");
        CheckPath(historyPath); CheckPath(clockPath);
        var history = await AutomaticBackupService.ReadRecordedAsync(server, ct);
        if (history.History.Count == 0 && history.PendingHour is null && history.QueuedHours.Count == 0) return;
        var clock = await CoordinatedBackupRecords.ReadBoundedAsync<LegacyBackupRetentionState>(clockPath, ct)
            ?? new(1, server.Id, clusterId, server.Edition, history.RuntimeTicks, 0, history.LastRuntimeObservation);
        if (clock.SchemaVersion != 1 || clock.ServerId != server.Id || clock.ClusterId != clusterId || clock.Edition != server.Edition
            || clock.OriginalRuntimeTicks != history.RuntimeTicks || clock.AdditionalRuntimeTicks < 0
            || clock.Observation is { } previous && (previous.HostSession == Guid.Empty || previous.CumulativeTicks < 0))
            throw new InvalidDataException("Unexpected legacy backup retention checkpoint.");
        if (observation is not null)
        {
            var delta = observation.CumulativeTicks - (clock.Observation?.HostSession == observation.HostSession ? clock.Observation.CumulativeTicks : 0);
            if (delta < 0) throw new InvalidDataException("Legacy retention runtime moved backwards.");
            clock = clock with { AdditionalRuntimeTicks = checked(clock.AdditionalRuntimeTicks + delta), Observation = observation };
        }
        var retention = new RuntimeBackupClock(TimeSpan.FromTicks(checked(clock.OriginalRuntimeTicks + clock.AdditionalRuntimeTicks)));
        await new AtomicJsonStore<LegacyBackupRetentionState>(clockPath).SaveAsync(clock, ct);
        var expired = history.History.Where(entry => !retention.ShouldRetain(entry.RuntimeHour)).ToArray();
        foreach (var entry in expired)
        {
            var path = Path.Combine(folder, entry.FileName);
            CheckPath(path);
            if (Directory.Exists(path)) throw new IOException("A legacy archive location contains a folder. Retention cleanup needs review.");
        }
        foreach (var entry in expired)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(folder, entry.FileName);
            if (File.Exists(path)) File.Delete(path);
        }
        var pending = history.PendingHour is long hour && retention.ShouldRetain(hour) ? history.PendingHour : null;
        var queue = history.QueuedHours.Where(retention.ShouldRetain).ToList();
        if (pending is null && queue.Count > 0) { pending = queue[0]; queue.RemoveAt(0); }
        await new AtomicJsonStore<AutomaticBackupState>(historyPath).SaveAsync(history with
        {
            History = history.History.Where(entry => retention.ShouldRetain(entry.RuntimeHour)).ToList(),
            PendingHour = pending, QueuedHours = queue, RetryAt = pending is null ? null : history.RetryAt,
            LastError = pending is null && queue.Count == 0 ? null : history.LastError
        }, ct);
    }
    private static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Legacy retention must not follow linked storage.");
    }
}
