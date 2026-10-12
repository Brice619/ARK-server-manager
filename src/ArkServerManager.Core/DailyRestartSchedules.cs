using System.Collections.Concurrent;

namespace ArkServerManager.Core;

public sealed record DailyRestartSchedule(Guid ServerId, string Directory, ArkEdition Edition, TimeOnly Time,
    bool Enabled, DateOnly? LastAttemptDate = null);
public sealed record DailyRestartDocument(int SchemaVersion, List<DailyRestartSchedule> Schedules);
public sealed record DailyRestartSave(Guid ServerId, TimeOnly Time, bool Enabled, bool Confirmed);

public sealed class DailyRestartSchedules(CatalogRepository repository, string file)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = Gates.GetOrAdd(Path.GetFullPath(file), _ => new(1, 1));
    private readonly AtomicJsonStore<DailyRestartDocument> store = new(file);
    private readonly ConcurrentDictionary<Guid, string> scheduleProblems = new();

    public async Task RemoveDeletedAsync(ServerDeletionRecord deletion, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            DeletedServerRecordCleanup.RequireCompleted(await repository.ReadAsync(ct), deletion);
            var document = await LoadAsync(ct);
            var entry = document.Schedules.SingleOrDefault(p => p.ServerId == deletion.ServerId);
            if (entry is null) return;
            DeletedServerRecordCleanup.RequireDirectory(deletion, entry.Directory);
            await store.SaveAsync(document with { Schedules = document.Schedules.Where(p => p.ServerId != deletion.ServerId).ToList() }, ct);
            scheduleProblems.TryRemove(deletion.ServerId, out _);
        }
        finally { gate.Release(); }
    }

    public async Task RunAsync(Func<Guid, CancellationToken, Task<LifecycleOperationResult>> restartRunning,
        Func<Guid, string, CancellationToken, Task> report, CancellationToken ct)
    {
        string? previousProblem = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { await CycleAsync(DateTimeOffset.UtcNow, TimeZoneInfo.Local, restartRunning, report, ct); previousProblem = null; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                if (previousProblem != error.Message) await report(Guid.Empty, "Restart scheduling needs attention: " + error.Message, ct);
                previousProblem = error.Message;
            }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    public async Task<DailyRestartSchedule> ReadAsync(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == id);
            var saved = (await LoadAsync(ct)).Schedules.SingleOrDefault(s => s.ServerId == id);
            if (saved is not null) RequireBinding(saved, server);
            return saved ?? new(id, server.Directory, server.Edition, new(4, 0), false);
        }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(Guid id, TimeOnly time, bool enabled, bool confirmed, CancellationToken ct = default)
    {
        if (!confirmed || time.Second != 0 || time.Millisecond != 0 || time.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new ArgumentException("Confirm a restart time in whole minutes.");
        await gate.WaitAsync(ct);
        try
        {
            var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == id);
            var document = await LoadAsync(ct);
            var previous = document.Schedules.SingleOrDefault(s => s.ServerId == id);
            if (previous is not null) RequireBinding(previous, server);
            document.Schedules.RemoveAll(s => s.ServerId == id);
            document.Schedules.Add(new(id, server.Directory, server.Edition, time, enabled, previous?.LastAttemptDate));
            await store.SaveAsync(document, ct);
        }
        finally { gate.Release(); }
    }
    public async Task CycleAsync(DateTimeOffset now, TimeZoneInfo zone,
        Func<Guid, CancellationToken, Task<LifecycleOperationResult>> restartRunning,
        Func<Guid, string, CancellationToken, Task> report, CancellationToken ct = default)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var date = DateOnly.FromDateTime(local.DateTime); var time = TimeOnly.FromDateTime(local.DateTime);
        var catalog = await repository.ReadAsync(ct);
        foreach (var server in catalog.Servers)
        {
            var due = false;
            await gate.WaitAsync(ct);
            try
            {
                var document = await LoadAsync(ct);
                var saved = document.Schedules.SingleOrDefault(s => s.ServerId == server.Id);
                if (saved is null || !saved.Enabled) { scheduleProblems.TryRemove(server.Id, out _); continue; }
                RequireBinding(saved, server);
                scheduleProblems.TryRemove(server.Id, out _);
                if (saved.Time.Hour != time.Hour || saved.Time.Minute != time.Minute || saved.LastAttemptDate >= date) continue;
                document.Schedules[document.Schedules.IndexOf(saved)] = saved with { LastAttemptDate = date };
                // Record acceptance before executing, including if the manager exits during restart.
                await store.SaveAsync(document, ct); due = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                var message = "Restart schedule needs attention: " + error.Message;
                if (scheduleProblems.GetValueOrDefault(server.Id) != message)
                {
                    await report(server.Id, message, ct);
                    scheduleProblems[server.Id] = message;
                }
            }
            finally { gate.Release(); }
            if (!due) continue;
            try
            {
                var result = await restartRunning(server.Id, ct);
                if (!result.Success) await report(server.Id, "Scheduled restart needs attention: " + string.Join(" ", result.Members.Select(s => s.Error)), ct);
                else if (result.Members.Any(s => s.State == ServerState.Running)) await report(server.Id, "Scheduled map restart completed.", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { await report(server.Id, "Scheduled restart failed: " + error.Message, ct); }
        }
    }
    private async Task<DailyRestartDocument> LoadAsync(CancellationToken ct)
    {
        var document = await store.LoadAsync(ct) ?? new(1, []);
        if (document.SchemaVersion != 1 || document.Schedules is null || document.Schedules.Any(s => s is null)
            || document.Schedules.Select(s => s.ServerId).Distinct().Count() != document.Schedules.Count
            || document.Schedules.Any(s => s.ServerId == Guid.Empty || !Path.IsPathFullyQualified(s.Directory) || !Enum.IsDefined(s.Edition)
                || s.Time.Ticks % TimeSpan.TicksPerMinute != 0)) throw new InvalidDataException("Review the saved restart schedules.");
        return document;
    }
    private static void RequireBinding(DailyRestartSchedule schedule, ServerDefinition server)
    {
        if (schedule.Directory != server.Directory || schedule.Edition != server.Edition)
            throw new InvalidOperationException("The scheduled installation changed. Review its restart schedule.");
    }
}
