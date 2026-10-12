namespace ArkServerManager.Core;

public sealed record CoordinatedBackupSchedule(int SchemaVersion, Guid ClusterId, ArkEdition Edition,
    long RuntimeTicks, RunningTimeObservation? Observation, List<long> PendingHours,
    DateTimeOffset? RetryAt, string? LastError, List<CoordinatedRestorePoint> History);

/// <summary>Caller owns the whole-cluster work scope, including checkpoint writes.</summary>
public sealed class CoordinatedBackupService(CatalogDocument catalog, Guid clusterId, string root,
    Func<long, CancellationToken, Task<CoordinatedRestorePoint>> publish, Func<string, Task> report,
    Func<long, CancellationToken, Task>? expire = null)
{
    public async Task<CoordinatedBackupSchedule> TickAsync(RunningTimeObservation observation, DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (observation.HostSession == Guid.Empty || observation.CumulativeTicks < 0)
            throw new ArgumentException("Choose a valid measured cluster runtime.");
        var cluster = catalog.Clusters.Single(c => c.Id == clusterId);
        var storage = ManagedClusterStorage.Resolve(catalog, clusterId, root);
        var schedulePath = Path.Combine(storage.Backups, "Coordinated", "schedule.json");
        CheckSchedulePath(schedulePath);
        var store = new AtomicJsonStore<CoordinatedBackupSchedule>(schedulePath);
        var state = await CoordinatedBackupRecords.ReadBoundedAsync<CoordinatedBackupSchedule>(schedulePath, ct)
            ?? new(1, clusterId, cluster.Edition, 0, null, [], null, null, []);
        Validate(state);
        var delta = observation.CumulativeTicks - (state.Observation?.HostSession == observation.HostSession
            ? state.Observation.CumulativeTicks : 0);
        if (delta < 0) throw new InvalidDataException("Cluster runtime moved backwards within this Host session.");
        var clock = new RuntimeBackupClock(TimeSpan.FromTicks(state.RuntimeTicks));
        var due = clock.Advance(TimeSpan.FromTicks(delta), true);
        state = state with { RuntimeTicks = clock.Runtime.Ticks, Observation = observation,
            PendingHours = state.PendingHours.Concat(due.Select(s => s.Hour)).Distinct().Order().ToList() };
        await store.SaveAsync(state, ct);
        try
        {
            foreach (var expired in state.History.Select(p => p.RuntimeHour).Concat(state.PendingHours)
                .Distinct().Where(h => !clock.ShouldRetain(h)))
            {
                if (expire is null) throw new NotSupportedException("Coordinated retention cleanup is not configured.");
                await expire(expired, ct);
            }
            state = state with { History = state.History.Where(p => clock.ShouldRetain(p.RuntimeHour)).ToList(),
                PendingHours = state.PendingHours.Where(clock.ShouldRetain).ToList() };
            await store.SaveAsync(state, ct);
            if (state.PendingHours.Count == 0 || state.RetryAt > now) return state;
            var hour = state.PendingHours[0];
            var point = await publish(hour, ct);
            if (point.SchemaVersion != 1 || point.ClusterId != clusterId || point.Edition != cluster.Edition
                || point.RuntimeHour != hour || point.Capture is null || point.Capture.Id == Guid.Empty
                || point.Capture.CapturedAt == default || point.Maps is null || point.Maps.Count == 0
                || point.Maps.Any(id => id == Guid.Empty) || point.Maps.Distinct().Count() != point.Maps.Count)
                throw new InvalidDataException("Unexpected coordinated publication result.");
            state = state with { PendingHours = state.PendingHours.Skip(1).ToList(), RetryAt = null, LastError = null,
                History = state.History.Where(p => p.RuntimeHour != hour).Append(point).OrderBy(p => p.RuntimeHour).ToList() };
            await store.SaveAsync(state, ct);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or TimeoutException or InvalidOperationException or NotSupportedException
            || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            state = state with { RetryAt = now.AddMinutes(1), LastError = error.Message };
            await store.SaveAsync(state, ct);
            await report("Coordinated backup remains scheduled for retry: " + error.Message);
        }
        return state;

        void Validate(CoordinatedBackupSchedule saved)
        {
            var completed = saved.RuntimeTicks / TimeSpan.TicksPerHour;
            if (saved.SchemaVersion != 1 || saved.ClusterId != clusterId || saved.Edition != cluster.Edition
                || saved.RuntimeTicks < 0 || saved.PendingHours is null || saved.History is null
                || saved.PendingHours.Any(h => h < 1 || h > completed)
                || !saved.PendingHours.SequenceEqual(saved.PendingHours.Distinct().Order())
                || saved.Observation is { } previous && (previous.HostSession == Guid.Empty || previous.CumulativeTicks < 0)
                || saved.History.Any(p => p is null || p.SchemaVersion != 1 || p.ClusterId != clusterId
                    || p.Edition != cluster.Edition || p.RuntimeHour < 1 || p.RuntimeHour > completed
                    || p.Capture is null || p.Capture.Id == Guid.Empty || p.Capture.CapturedAt == default
                    || p.Maps is null || p.Maps.Count == 0 || p.Maps.Any(id => id == Guid.Empty)
                    || p.Maps.Distinct().Count() != p.Maps.Count)
                || saved.History.Select(p => p.RuntimeHour).Distinct().Count() != saved.History.Count)
                throw new InvalidDataException("Unexpected coordinated backup schedule.");
        }
    }

    private static void CheckSchedulePath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Coordinated backup schedules must not follow linked storage.");
    }
}
