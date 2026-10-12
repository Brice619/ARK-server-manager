using System.Collections.Concurrent;

namespace ArkServerManager.Core;

public interface IServerRuntime
{
    ServerState State { get; }
    Task StartAsync(ServerDefinition definition, CancellationToken ct);
    Task<bool> StopAsync(CancellationToken ct);
}

public enum LifecycleAction { Start, Stop, Restart }
public interface IRunningTimeRuntime : IServerRuntime
{
    TimeSpan RunningTime { get; }
}
public sealed record MemberOperationResult(Guid ServerId, ServerState State, bool Success, string? Error)
{
    public bool Managed { get; init; }
    public long? RunningTicks { get; init; }
}
public sealed record LifecycleOperationResult(IReadOnlyList<MemberOperationResult> Members)
{
    public bool Success => Members.All(m => m.Success);
}

/// <summary>Host-owned serialization of cluster user actions. Runtime recovery remains member-local.</summary>
public sealed class ServerLifecycleCoordinator(
    CatalogRepository repository,
    Func<Guid, IServerRuntime> createRuntime,
    Func<Guid, CancellationToken, Task> beforeStart,
    SemaphoreSlim? fileOperationGate = null,
    Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>>? verifyUnownedStopped = null,
    Func<ServerDefinition, CancellationToken, Task>? afterVerifiedStart = null,
    Func<ServerDefinition, CancellationToken, Task>? afterVerifiedStop = null)
{
    private readonly SemaphoreSlim gate = fileOperationGate ?? new(1, 1);
    private readonly ConcurrentDictionary<Guid, IServerRuntime> runtimes = new();

    /// <summary>Coordinates save requests and one captured view of every map plus shared transfer state.</summary>
    public Task<CoordinatedClusterBackupState> AcquireClusterBackupStateAsync(Guid clusterId, string managementRoot,
        Func<ServerDefinition, CancellationToken, Task> saveWorld,
        Func<IReadOnlyList<string>, CancellationToken, Task<IStableBackupSet>> captureRunning,
        Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped,
        CancellationToken ct = default)
        => AcquireClusterBackupStateCoreAsync(clusterId, managementRoot, saveWorld, captureRunning, verifyStopped, true, ct);

    private async Task<CoordinatedClusterBackupState> AcquireClusterBackupStateCoreAsync(Guid clusterId, string managementRoot,
        Func<ServerDefinition, CancellationToken, Task> saveWorld,
        Func<IReadOnlyList<string>, CancellationToken, Task<IStableBackupSet>> captureRunning,
        Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped,
        bool ownsGate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(saveWorld); ArgumentNullException.ThrowIfNull(captureRunning);
        ArgumentNullException.ThrowIfNull(verifyStopped);
        if (ownsGate) await gate.WaitAsync(ct);
        IStableBackupSet? captured = null;
        var transferred = false;
        try
        {
            var catalog = await repository.ReadAsync(ct);
            var storage = ManagedClusterStorage.Resolve(catalog, clusterId, managementRoot);
            var members = catalog.Servers.Where(s => s.ClusterId == clusterId).OrderBy(s => s.Id).ToArray();
            if (members.Length == 0) throw new InvalidOperationException("The cluster has no maps to capture.");
            var running = new List<ServerDefinition>();
            foreach (var member in members)
            {
                await RequireBackupTargetAsync(member, ct);
                var savedDirectory = Path.Combine(member.Directory, "ShooterGame", "Saved");
                if (!Directory.Exists(savedDirectory))
                    throw new DirectoryNotFoundException("A cluster map's saved-state directory is missing.");
                for (var path = Path.GetFullPath(savedDirectory); path is not null; path = Path.GetDirectoryName(path))
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Cluster map saved state must not use linked paths.");
                var state = State(member.Id);
                if (state == ServerState.Running && runtimes.ContainsKey(member.Id)) running.Add(member);
                else if (state is ServerState.Stopped or ServerState.Failed)
                {
                    var check = await verifyStopped(member, ct);
                    if (check.State != InstallationStopState.Stopped || check.MeasuredAt == default
                        || !string.Equals(check.Directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(member.Directory)),
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new InvalidOperationException($"Cluster map '{member.Settings.Name}' could not be verified as stopped ({check.State}); no coordinated capture was made. "
                            + string.Join(" ", check.Findings));
                }
                else throw new InvalidOperationException("Wait for the cluster maps' current lifecycle actions before capturing them.");
            }
            storage = ManagedClusterStorage.Prepare(catalog, clusterId, managementRoot);
            var sources = members.Select(s => Path.GetFullPath(Path.Combine(s.Directory, "ShooterGame", "Saved")))
                .Append(storage.Transfers).ToArray();
            foreach (var member in running) await saveWorld(member, ct);
            if (running.Any(member => State(member.Id) != ServerState.Running))
                throw new InvalidOperationException("A cluster map changed state before capture.");
            captured = running.Count > 0 ? await captureRunning(sources, ct) : new StoppedBackupSet(sources);
            if (captured.Capture.Id == Guid.Empty || captured.Capture.CapturedAt == default)
                throw new InvalidDataException("A coordinated backup needs a valid capture identity.");
            foreach (var source in sources)
            {
                await using var view = captured.GetState(source);
                if (view.Capture != captured.Capture || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(view.SourceDirectory)),
                    Path.TrimEndingDirectorySeparator(source), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("A coordinated backup view belongs to another source directory.");
            }
            ct.ThrowIfCancellationRequested();
            var lease = new CoordinatedClusterBackupState(catalog, clusterId, storage, captured, ownsGate ? gate : null);
            transferred = true;
            return lease;
        }
        finally
        {
            if (!transferred)
            {
                try { if (captured is not null) await captured.DisposeAsync(); }
                finally { if (ownsGate) gate.Release(); }
            }
        }
    }

    /// <summary>Protects cluster checkpoints, retention and captures as one work scope. Do not re-enter lifecycle operations.</summary>
    public async Task WithClusterBackupWorkAsync(Guid clusterId, string managementRoot,
        Func<ServerDefinition, CancellationToken, Task> saveWorld,
        Func<IReadOnlyList<string>, CancellationToken, Task<IStableBackupSet>> captureRunning,
        Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped,
        Func<CatalogDocument, Func<CancellationToken, Task<CoordinatedClusterBackupState>>, CancellationToken, Task> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(saveWorld); ArgumentNullException.ThrowIfNull(captureRunning);
        ArgumentNullException.ThrowIfNull(verifyStopped); ArgumentNullException.ThrowIfNull(work);
        await gate.WaitAsync(ct);
        var active = true;
        var leases = new List<CoordinatedClusterBackupState>();
        try
        {
            var catalog = await repository.ReadAsync(ct);
            ManagedClusterStorage.Resolve(catalog, clusterId, managementRoot);
            var members = catalog.Servers.Where(s => s.ClusterId == clusterId).ToArray();
            if (members.Length == 0) throw new InvalidOperationException("The cluster has no maps to back up.");
            foreach (var member in members) await RequireBackupTargetAsync(member, ct);
            await work(catalog, async token =>
            {
                if (!active) throw new InvalidOperationException("The cluster backup work scope has ended.");
                var lease = await AcquireClusterBackupStateCoreAsync(clusterId, managementRoot,
                    saveWorld, captureRunning, verifyStopped, false, token);
                leases.Add(lease);
                return lease;
            }, ct);
        }
        finally
        {
            active = false;
            try
            {
                // Scope owns cleanup even if work fails after acquisition. Dispose is idempotent.
                Exception? cleanupFailure = null;
                foreach (var lease in leases)
                    try { await lease.DisposeAsync(); }
                    catch (Exception error) { cleanupFailure ??= error; }
                if (cleanupFailure is not null) throw new IOException("Cluster backup views could not all be released cleanly.", cleanupFailure);
            }
            finally { gate.Release(); }
        }
    }

    private sealed class StoppedBackupSet(IReadOnlyList<string> sources) : IStableBackupSet
    {
        public SnapshotCapture Capture { get; } = new(Guid.NewGuid(), DateTimeOffset.UtcNow);
        private int disposed;
        public IStableServerState GetState(string sourceDirectory)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var source = Path.GetFullPath(sourceDirectory);
            if (!sources.Contains(source, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                throw new ArgumentException("The requested source is not part of this cluster capture.");
            return new StoppedBackupState(source, Capture);
        }
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref disposed, 1); return ValueTask.CompletedTask; }
    }

    /// <summary>Excludes lifecycle/file mutation until the acquired backup view has been archived and released.</summary>
    public async Task<IStableServerState> AcquireBackupStateAsync(ServerDefinition expected,
        Func<ServerDefinition, CancellationToken, Task> saveWorld,
        Func<string, CancellationToken, Task<IStableServerState>> captureRunning,
        Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(saveWorld); ArgumentNullException.ThrowIfNull(captureRunning);
        ArgumentNullException.ThrowIfNull(verifyStopped);
        await gate.WaitAsync(ct);
        var transferred = false;
        try
        {
            var captured = await CreateBackupStateWhileLockedAsync(expected, saveWorld, captureRunning, verifyStopped, ct);
            var lease = new ProtectedBackupState(captured, gate);
            transferred = true;
            return lease;
        }
        finally { if (!transferred) gate.Release(); }
    }

    /// <summary>Protects checkpoint, retention and archive work together. Callback must dispose acquired views and not re-enter the coordinator.</summary>
    public async Task WithBackupWorkAsync(ServerDefinition expected,
        Func<ServerDefinition, CancellationToken, Task> saveWorld,
        Func<string, CancellationToken, Task<IStableServerState>> captureRunning,
        Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped,
        Func<Func<CancellationToken, Task<IStableServerState>>, CancellationToken, Task> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(saveWorld); ArgumentNullException.ThrowIfNull(captureRunning);
        ArgumentNullException.ThrowIfNull(verifyStopped); ArgumentNullException.ThrowIfNull(work);
        await gate.WaitAsync(ct);
        var active = true;
        try
        {
            await RequireBackupTargetAsync(expected, ct);
            await work(token =>
            {
                if (!active) throw new InvalidOperationException("The backup work scope has ended.");
                return CreateBackupStateWhileLockedAsync(expected, saveWorld, captureRunning, verifyStopped, token);
            }, ct);
        }
        finally { active = false; gate.Release(); }
    }

    private async Task<ServerDefinition> RequireBackupTargetAsync(ServerDefinition expected, CancellationToken ct)
    {
        var document = await repository.ReadAsync(ct);
        var selected = document.Servers.SingleOrDefault(s => s.Id == expected.Id)
            ?? throw new InvalidOperationException("The backup target was removed.");
        if (selected.Directory != expected.Directory || selected.Edition != expected.Edition
            || selected.Map != expected.Map || selected.ClusterId != expected.ClusterId)
            throw new InvalidOperationException("The backup target changed before capture.");
        if (!Directory.Exists(selected.Directory))
            throw new DirectoryNotFoundException("The registered backup installation is missing.");
        for (var path = Path.GetFullPath(selected.Directory); path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The backup installation must not use linked paths.");
        return selected;
    }

    private async Task<IStableServerState> CreateBackupStateWhileLockedAsync(ServerDefinition expected,
        Func<ServerDefinition, CancellationToken, Task> saveWorld,
        Func<string, CancellationToken, Task<IStableServerState>> captureRunning,
        Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped, CancellationToken ct)
    {
        IStableServerState? captured = null;
        try
        {
            var selected = await RequireBackupTargetAsync(expected, ct);
            var source = Path.GetFullPath(Path.Combine(selected.Directory, "ShooterGame", "Saved"));
            var state = State(selected.Id);
            if (state == ServerState.Running && runtimes.ContainsKey(selected.Id))
            {
                await saveWorld(selected, ct);
                if (State(selected.Id) != ServerState.Running)
                    throw new InvalidOperationException("The map changed state before its backup could be captured.");
                captured = await captureRunning(source, ct);
            }
            else if (state is ServerState.Stopped or ServerState.Failed)
            {
                var check = await verifyStopped(selected, ct);
                if (check.State != InstallationStopState.Stopped || check.MeasuredAt == default
                    || !string.Equals(check.Directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected.Directory)),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("The backup target could not be verified as stopped.");
                captured = new StoppedBackupState(source);
            }
            else throw new InvalidOperationException("Wait for the map's current lifecycle action before backing it up.");
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(captured.SourceDirectory)),
                Path.TrimEndingDirectorySeparator(source), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("The backup view belongs to another state directory.");
            ct.ThrowIfCancellationRequested();
            return captured;
        }
        catch
        {
            if (captured is not null) await captured.DisposeAsync();
            throw;
        }
    }

    private sealed class StoppedBackupState(string directory, SnapshotCapture? capture = null) : IStableServerState
    {
        public SnapshotCapture? Capture => capture;
        public string Directory => directory;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ProtectedBackupState(IStableServerState captured, SemaphoreSlim gate) : IStableServerState
    {
        public SnapshotCapture? Capture => captured.Capture;
        public string Directory => captured.Directory;
        public string SourceDirectory => captured.SourceDirectory;
        private int disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { await captured.DisposeAsync(); }
            finally { gate.Release(); }
        }
    }

    /// <summary>Recovery may outlive registration removal. Checks both recorded locations while excluding host starts.</summary>
    public async Task WithStoppedDeletionRecoveryAsync(ServerDeletionRecord record,
        Func<CancellationToken, Task> change,
        Func<ServerDeletionRecord, CancellationToken, Task<bool>> verifyStopped,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(verifyStopped);
        await gate.WaitAsync(ct);
        try
        {
            if (State(record.ServerId) is not (ServerState.Stopped or ServerState.Failed))
                throw new InvalidOperationException("Stop the selected map before recovering its files.");
            if (!await verifyStopped(record, ct))
                throw new InvalidOperationException("The recovery locations could not be verified as stopped.");
            ct.ThrowIfCancellationRequested();
            await change(ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>External file-operation entry point; callbacks must not re-enter this coordinator or beforeStart.</summary>
    public Task WithStoppedServerAsync(ServerDefinition expected,
        Func<CancellationToken, Task> change,
        Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped,
        CancellationToken ct = default)
        => WithStoppedFilesAsync(expected, change, verifyStopped, includeCluster: true, ct);

    /// <summary>Map-only file scope for restoration; does not stop or verify cluster siblings.</summary>
    public Task WithStoppedMemberAsync(ServerDefinition expected,
        Func<CancellationToken, Task> change,
        Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped,
        CancellationToken ct = default)
        => WithStoppedFilesAsync(expected, change, verifyStopped, includeCluster: false, ct);

    /// <summary>Authorizes shared-state replacement only while every current member is verified stopped.</summary>
    public async Task WithStoppedClusterAsync(Guid clusterId, string managementRoot,
        Func<CatalogDocument, CancellationToken, Task> change,
        Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change); ArgumentNullException.ThrowIfNull(verifyStopped);
        await gate.WaitAsync(ct);
        try
        {
            var catalog = await repository.ReadAsync(ct);
            ManagedClusterStorage.Resolve(catalog, clusterId, managementRoot);
            var members = catalog.Servers.Where(s => s.ClusterId == clusterId).ToArray();
            if (members.Length == 0) throw new InvalidOperationException("Choose a cluster with registered maps.");
            foreach (var member in members)
            {
                await RequireBackupTargetAsync(member, ct);
                if (State(member.Id) is not (ServerState.Stopped or ServerState.Failed))
                    throw new InvalidOperationException("Stop all maps before replacing shared cluster data.");
                var check = await verifyStopped(member, ct);
                if (check.State != InstallationStopState.Stopped || check.MeasuredAt == default
                    || !string.Equals(check.Directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(member.Directory)),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Every map must be verified as stopped before replacing shared data.");
            }
            ct.ThrowIfCancellationRequested();
            await change(catalog, ct);
        }
        finally { gate.Release(); }
    }

    private async Task WithStoppedFilesAsync(ServerDefinition expected,
        Func<CancellationToken, Task> change,
        Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped,
        bool includeCluster, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(verifyStopped);
        await gate.WaitAsync(ct);
        try
        {
            var document = await repository.ReadAsync(ct);
            var selected = document.Servers.SingleOrDefault(s => s.Id == expected.Id)
                ?? throw new InvalidOperationException("The selected server was removed.");
            if (selected.Directory != expected.Directory || selected.Edition != expected.Edition || selected.ClusterId != expected.ClusterId)
                throw new InvalidOperationException("The selected installation changed. Review it before saving.");
            var targets = includeCluster && selected.ClusterId is Guid clusterId
                ? document.Servers.Where(s => s.ClusterId == clusterId).ToArray() : [selected];
            foreach (var target in targets)
            {
                if (State(target.Id) is not (ServerState.Stopped or ServerState.Failed))
                    throw new InvalidOperationException("Stop the server or cluster before changing its files.");
                var check = await verifyStopped(target, ct);
                if (check.State != InstallationStopState.Stopped || check.MeasuredAt == default
                    || !string.Equals(check.Directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Directory)),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException(includeCluster ? "The server or cluster could not be verified as stopped."
                        : "Stop the selected map before changing its files; its stopped state could not be verified.");
            }
            ct.ThrowIfCancellationRequested();
            // All host-owned start/stop requests remain excluded throughout this file operation.
            await change(ct);
        }
        finally { gate.Release(); }
    }

    public async Task WithStatusScopeAsync(Func<IReadOnlyList<MemberOperationResult>, CancellationToken, Task> work,
        CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var catalog = await repository.ReadAsync(ct);
            var statuses = catalog.Servers.Select(s => new MemberOperationResult(s.Id, State(s.Id), true, null)
                { Managed = runtimes.ContainsKey(s.Id) }).ToArray();
            await work(statuses, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<MemberOperationResult>> StatusAsync(CancellationToken ct = default)
    {
        // Display observations must remain available during long native starts or
        // downloads. File-changing decisions still use the serialized status scope.
        var catalog = await repository.ReadAsync(ct);
        return catalog.Servers.Select(s =>
        {
            runtimes.TryGetValue(s.Id, out var runtime);
            return new MemberOperationResult(s.Id, runtime?.State ?? ServerState.Stopped, true, null)
            { Managed = runtime is not null, RunningTicks = runtime is IRunningTimeRuntime measured ? measured.RunningTime.Ticks : null };
        }).ToArray();
    }

    /// <summary>Gracefully stops every runtime owned by this host, including removed registrations.</summary>
    public async Task<LifecycleOperationResult> StopAllAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var results = new List<MemberOperationResult>();
            var catalog = await repository.ReadAsync(ct);
            // Without a process verifier this coordinator can speak only for owned runtimes.
            var verifiedRegistrations = verifyUnownedStopped is null ? Enumerable.Empty<Guid>() : catalog.Servers.Select(s => s.Id);
            foreach (var id in runtimes.Keys.Concat(verifiedRegistrations).Distinct())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var stopped = !runtimes.TryGetValue(id, out var runtime) || await runtime.StopAsync(ct);
                    var registered = catalog.Servers.SingleOrDefault(s => s.Id == id);
                    if (stopped && registered is not null && verifyUnownedStopped is not null)
                        stopped = (await verifyUnownedStopped(registered, ct)).State == InstallationStopState.Stopped;
                    results.Add(new(id, State(id), stopped,
                        stopped ? null : "The server did not stop and was left running."));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) { results.Add(new(id, State(id), false, error.Message)); }
            }
            return new(results);
        }
        finally { gate.Release(); }
    }

    public Task<LifecycleOperationResult> ExecuteAsync(Guid serverId, LifecycleAction action, CancellationToken ct = default)
        => ExecuteScopedAsync(serverId, action, includeCluster: true, ct);

    /// <summary>Explicit map-only action used by single-map restoration.</summary>
    public Task<LifecycleOperationResult> ExecuteMemberAsync(Guid serverId, LifecycleAction action, CancellationToken ct = default)
        => ExecuteScopedAsync(serverId, action, includeCluster: false, ct);

    /// <summary>Schedules may restart only a currently running owned map, checked under the lifecycle gate.</summary>
    public Task<LifecycleOperationResult> RestartRunningMemberAsync(Guid serverId, CancellationToken ct = default)
        => ExecuteScopedAsync(serverId, LifecycleAction.Restart, includeCluster: false, ct, requireRunning: true);

    private async Task<LifecycleOperationResult> ExecuteScopedAsync(Guid serverId, LifecycleAction action,
        bool includeCluster, CancellationToken ct, bool requireRunning = false)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        await gate.WaitAsync(ct);
        try
        {
            var document = await repository.ReadAsync(ct);
            var selected = document.Servers.SingleOrDefault(s => s.Id == serverId)
                ?? throw new ArgumentException("The server no longer exists.");
            if (requireRunning && State(serverId) != ServerState.Running)
                return new([new(serverId, State(serverId), true, null)]);
            var targets = includeCluster && selected.ClusterId is Guid clusterId
                ? document.Servers.Where(s => s.ClusterId == clusterId).Select(s => s.Id).ToList()
                : [serverId];
            var results = new Dictionary<Guid, MemberOperationResult>();
            if (action is LifecycleAction.Stop or LifecycleAction.Restart)
                foreach (var id in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        bool stopped;
                        if (runtimes.TryGetValue(id, out var runtime)) stopped = await runtime.StopAsync(ct);
                        else stopped = true;
                        if (stopped && verifyUnownedStopped is not null)
                            stopped = (await verifyUnownedStopped(document.Servers.Single(s => s.Id == id), ct)).State == InstallationStopState.Stopped;
                        if (stopped && afterVerifiedStop is not null)
                            await afterVerifiedStop(document.Servers.Single(s => s.Id == id), ct);
                        results[id] = new(id, State(id), stopped, stopped ? null : "The server did not stop and was left running.");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) { results[id] = new(id, State(id), false, error.Message); }
                }
            if (action is LifecycleAction.Start or LifecycleAction.Restart)
                for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                {
                    var id = targets[targetIndex];
                    ct.ThrowIfCancellationRequested();
                    if (results.TryGetValue(id, out var stopped) && !stopped.Success) continue;
                    if (State(id) is not (ServerState.Stopped or ServerState.Failed))
                    {
                        results[id] = new(id, State(id), true, null);
                        continue;
                    }
                    try
                    {
                        // Pending settings and queued updates must finish before launch uses fresh catalog data.
                        await beforeStart(id, ct);
                        var latestDocument = await repository.ReadAsync(ct);
                        var latest = latestDocument.Servers.SingleOrDefault(s => s.Id == id)
                            ?? throw new InvalidOperationException("The server was removed before startup.");
                        // Recovery may publish an accepted standalone-to-cluster join before launch.
                        // A group Start then follows that newly published group; member-only actions stay scoped.
                        if (includeCluster && selected.ClusterId is null && latest.ClusterId is Guid joinedCluster)
                            foreach (var member in latestDocument.Servers.Where(s => s.ClusterId == joinedCluster))
                                if (!targets.Contains(member.Id)) targets.Add(member.Id);
                        if (!runtimes.TryGetValue(id, out var runtime))
                            if (!runtimes.TryAdd(id, runtime = createRuntime(id)))
                                throw new InvalidOperationException("The server runtime was already registered.");
                        await runtime.StartAsync(latest, ct);
                        if (runtime.State == ServerState.Running && afterVerifiedStart is not null)
                            await afterVerifiedStart(latest, ct);
                        results[id] = new(id, runtime.State, true, null);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) { results[id] = new(id, State(id), false, error.Message); }
                }
            return new(targets.Select(id => results[id]).ToArray());
        }
        finally { gate.Release(); }
    }

    private ServerState State(Guid id) => runtimes.TryGetValue(id, out var runtime) ? runtime.State : ServerState.Stopped;
}
