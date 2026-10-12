using System.Collections.Concurrent;

namespace ArkServerManager.Core;

public interface IStableServerState : IAsyncDisposable
{
    string Directory { get; }
    string SourceDirectory => Directory;
    SnapshotCapture? Capture => null;
}

public sealed record BackupHistoryEntry(long RuntimeHour, string FileName, DateTimeOffset CreatedAt);
public sealed record RunningTimeObservation(Guid HostSession, long CumulativeTicks);
public sealed record AutomaticBackupState(int SchemaVersion, Guid ServerId, ArkEdition Edition,
    long RuntimeTicks, long? PendingHour, DateTimeOffset? RetryAt, List<BackupHistoryEntry> History, string? LastError)
{
    public List<long> QueuedHours { get; init; } = [];
    public RunningTimeObservation? LastRuntimeObservation { get; init; }
    public SnapshotScope Scope { get; init; }
}

/// <summary>Automatic per-server execution. A stable-state provider must coordinate the actual ARK save.</summary>
public sealed class AutomaticBackupService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Guid id;
    private readonly ArkEdition edition;
    private readonly string directory;
    private readonly AtomicJsonStore<AutomaticBackupState> store;
    private readonly SemaphoreSlim gate;
    private readonly TimeSpan retryDelay;
    private readonly Func<CancellationToken, Task<IStableServerState>> acquireStableState;
    private readonly Func<string, Task> report;
    private readonly SnapshotLayout layout;
    private readonly SnapshotScope scope;
    private readonly string? expectedSourceDirectory;
    private readonly SnapshotArchive archive = new();
    public Exception? ReportingFailure { get; private set; }

    public AutomaticBackupService(ServerDefinition server, TimeSpan retryDelay,
        Func<CancellationToken, Task<IStableServerState>> acquireStableState, Func<string, Task> report,
        SnapshotLayout layout = SnapshotLayout.Unspecified)
        : this(server.Id, server.Edition, Path.Combine(server.Directory, "backups"), SnapshotScope.Server,
            retryDelay, acquireStableState, report, layout,
            layout == SnapshotLayout.WorldSavedFolders ? Path.Combine(server.Directory, "ShooterGame", "Saved") : null) { }

    public AutomaticBackupService(CatalogDocument catalog, Guid clusterId, string managementRoot, TimeSpan retryDelay,
        Func<CancellationToken, Task<IStableServerState>> acquireStableState, Func<string, Task> report)
        : this(clusterId, catalog.Clusters.Single(c => c.Id == clusterId).Edition,
            ManagedClusterStorage.Resolve(catalog, clusterId, managementRoot).Backups, SnapshotScope.SharedCluster,
            retryDelay, acquireStableState, report, SnapshotLayout.Unspecified,
            ManagedClusterStorage.Resolve(catalog, clusterId, managementRoot).Transfers) { }

    private AutomaticBackupService(Guid id, ArkEdition edition, string backupDirectory, SnapshotScope scope,
        TimeSpan retryDelay, Func<CancellationToken, Task<IStableServerState>> acquireStableState,
        Func<string, Task> report, SnapshotLayout layout, string? expectedSourceDirectory)
    {
        if (retryDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
        if (!Enum.IsDefined(layout)) throw new ArgumentOutOfRangeException(nameof(layout));
        this.layout = layout;
        this.id = id;
        this.edition = edition;
        this.scope = scope;
        this.expectedSourceDirectory = expectedSourceDirectory is null ? null : Path.GetFullPath(expectedSourceDirectory);
        directory = Path.GetFullPath(backupDirectory);
        store = new(Path.Combine(directory, "backup-history.json"));
        gate = Gates.GetOrAdd(directory, _ => new(1, 1));
        this.retryDelay = retryDelay;
        this.acquireStableState = acquireStableState;
        this.report = report;
    }

    public Task<AutomaticBackupState> TickAsync(TimeSpan elapsed, bool running, DateTimeOffset now, CancellationToken ct = default)
        => TickCoreAsync(elapsed, running, null, now, ct);

    /// <summary>Deduplicates cumulative runtime readings, including retries after publication or cancellation.</summary>
    public Task<AutomaticBackupState> TickMeasuredAsync(RunningTimeObservation observation, DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.HostSession == Guid.Empty || observation.CumulativeTicks < 0)
            throw new ArgumentException("Choose a valid host session and measured runtime.");
        return TickCoreAsync(TimeSpan.Zero, true, observation, now, ct);
    }

    private async Task<AutomaticBackupState> TickCoreAsync(TimeSpan elapsed, bool running,
        RunningTimeObservation? observation, DateTimeOffset now, CancellationToken ct)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        await gate.WaitAsync(ct);
        try
        {
            var state = await LoadAsync(ct);
            if (observation is not null)
            {
                var previous = state.LastRuntimeObservation;
                var delta = observation.CumulativeTicks - (previous?.HostSession == observation.HostSession ? previous.CumulativeTicks : 0);
                if (delta < 0) throw new InvalidDataException("Measured runtime moved backwards within one host session.");
                elapsed = TimeSpan.FromTicks(delta);
                state = state with { LastRuntimeObservation = observation };
            }
            var clock = new RuntimeBackupClock(TimeSpan.FromTicks(state.RuntimeTicks));
            var due = clock.Advance(elapsed, running);
            state = state with { RuntimeTicks = clock.Runtime.Ticks };
            if (due.Count > 0)
            {
                if (state.PendingHour is null) state = state with { PendingHour = due[0].Hour, RetryAt = null };
                else if (state.PendingHour != due[0].Hour && !state.QueuedHours.Contains(due[0].Hour))
                    state = state with { QueuedHours = [.. state.QueuedHours, due[0].Hour] };
            }
            var retainedQueue = state.QueuedHours.Where(clock.ShouldRetain).Order().ToList();
            if (state.PendingHour is long pending && !clock.ShouldRetain(pending))
            {
                state = state with { PendingHour = retainedQueue.Count > 0 ? retainedQueue[0] : null, RetryAt = null };
                if (retainedQueue.Count > 0) retainedQueue.RemoveAt(0);
            }
            state = state with { QueuedHours = retainedQueue };
            // Persist time and pending work before disk cleanup or archive publication.
            await store.SaveAsync(state, ct);
            try
            {
                foreach (var entry in state.History.Where(h => !clock.ShouldRetain(h.RuntimeHour)))
                {
                    var path = Path.Combine(directory, entry.FileName);
                    if (File.Exists(path))
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("A backup archive is a link; retention cleanup needs attention.");
                        File.Delete(path);
                    }
                }
                state = state with { History = state.History.Where(h => clock.ShouldRetain(h.RuntimeHour)).ToList() };
                await store.SaveAsync(state, ct);
                if (state.PendingHour is not long hour || state.RetryAt > now) return state;
                // Retry may occur after a normal stop; the stable provider must support stopped state too.
                var filename = FileName(hour);
                var target = Path.Combine(directory, filename);
                SnapshotManifest manifest;
                if (File.Exists(target))
                {
                    if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("An existing backup archive is a link; recovery needs attention.");
                    manifest = await archive.VerifyAsync(target, ct);
                }
                else
                {
                    await using var stable = await acquireStableState(ct);
                    if (expectedSourceDirectory is not null && !string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(stable.SourceDirectory)),
                        Path.TrimEndingDirectorySeparator(expectedSourceDirectory),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new InvalidDataException("The backup capture belongs to another state directory.");
                    manifest = await archive.CreateAsync(stable.Directory, target, id, edition, hour, ct, scope, layout, stable.Capture);
                }
                if (manifest.ServerId != id || manifest.Edition != edition || manifest.RuntimeHour != hour
                    || manifest.Scope != scope || manifest.Layout != layout)
                    throw new InvalidDataException("An existing backup does not match this server and runtime hour.");
                state = state with
                {
                    PendingHour = state.QueuedHours.Count > 0 ? state.QueuedHours[0] : null,
                    QueuedHours = state.QueuedHours.Skip(1).ToList(), RetryAt = null, LastError = null,
                    History = [.. state.History.Where(h => h.RuntimeHour != hour), new(hour, filename, manifest.CreatedAt)]
                };
                await store.SaveAsync(state, ct);
                return state;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException
                or InvalidOperationException or NotSupportedException || error is OperationCanceledException && !ct.IsCancellationRequested)
            {
                state = state with { LastError = error.Message, RetryAt = now + retryDelay };
                try { await store.SaveAsync(state, ct); }
                catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException)
                { await ReportAsync("Backup state could not be saved: " + saveError.Message); }
                await ReportAsync("Automatic backup failed and remains scheduled for retry: " + error.Message);
                return state;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<AutomaticBackupState> ReadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await LoadAsync(ct); }
        finally { gate.Release(); }
    }

    public static async Task<AutomaticBackupState> ReadRecordedAsync(ServerDefinition server, CancellationToken ct = default)
    {
        var folder = Path.GetFullPath(Path.Combine(server.Directory, "backups"));
        var historyGate = Gates.GetOrAdd(folder, _ => new(1, 1));
        await historyGate.WaitAsync(ct);
        try { return await ReadStateAsync(server.Id, server.Edition, folder, new(Path.Combine(folder, "backup-history.json")), ct); }
        finally { historyGate.Release(); }
    }

    public static async Task<AutomaticBackupState> ReadRecordedClusterAsync(CatalogDocument catalog, Guid clusterId,
        string managementRoot, CancellationToken ct = default)
    {
        var folder = ManagedClusterStorage.Resolve(catalog, clusterId, managementRoot).Backups;
        var edition = catalog.Clusters.Single(c => c.Id == clusterId).Edition;
        var historyGate = Gates.GetOrAdd(folder, _ => new(1, 1));
        await historyGate.WaitAsync(ct);
        try { return await ReadStateAsync(clusterId, edition, folder, new(Path.Combine(folder, "backup-history.json")), ct, SnapshotScope.SharedCluster); }
        finally { historyGate.Release(); }
    }

    private Task<AutomaticBackupState> LoadAsync(CancellationToken ct) => ReadStateAsync(id, edition, directory, store, ct, scope);

    private static async Task<AutomaticBackupState> ReadStateAsync(Guid id, ArkEdition edition, string directory,
        AtomicJsonStore<AutomaticBackupState> store, CancellationToken ct, SnapshotScope scope = SnapshotScope.Server)
    {
        if (System.IO.Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The backup folder must not be a link.");
        var state = await store.LoadAsync(ct) ?? new(1, id, edition, 0, null, null, [], null) { Scope = scope };
        if (state.SchemaVersion != 1 || state.ServerId != id || state.Edition != edition || state.Scope != scope || state.RuntimeTicks < 0
            || state.History is null || state.History.Any(h => h.RuntimeHour < 1 || h.FileName != FileName(h.RuntimeHour))
            || state.History.GroupBy(h => h.RuntimeHour).Any(g => g.Count() > 1)
            || state.PendingHour is <= 0 || state.QueuedHours is null || state.QueuedHours.Any(h => h <= 0)
            || state.QueuedHours.Distinct().Count() != state.QueuedHours.Count || state.QueuedHours.Contains(state.PendingHour ?? -1))
            throw new InvalidDataException("Invalid automatic backup history.");
        if (state.LastRuntimeObservation is { } observed && (observed.HostSession == Guid.Empty || observed.CumulativeTicks < 0))
            throw new InvalidDataException("Invalid measured runtime checkpoint.");
        return state;
    }

    private static string FileName(long hour) => $"hour-{hour:D8}.zip";
    private async Task ReportAsync(string message)
    {
        try { await report(message); }
        catch (Exception error) { ReportingFailure = error; }
    }
}
