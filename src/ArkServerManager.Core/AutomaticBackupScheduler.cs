namespace ArkServerManager.Core;

/// <summary>Minute-aligned host scheduling with measured runtime and isolated member failures.</summary>
public sealed class AutomaticBackupScheduler
{
    private readonly CatalogRepository repository;
    private readonly Func<CancellationToken, Task<IReadOnlyList<MemberOperationResult>>> readRuntime;
    private readonly Func<ServerDefinition, RunningTimeObservation?, DateTimeOffset, CancellationToken, Task> executeTick;
    private readonly Func<Guid?, string, Task> report;
    private readonly TimeProvider time;
    private readonly Func<Guid, TimeSpan>? readClusterRuntime;
    private readonly Func<CatalogDocument, ClusterDefinition, RunningTimeObservation, DateTimeOffset, CancellationToken, Task>? executeClusterTick;
    private readonly Func<CancellationToken, Task>? maintenance;
    private readonly Guid session = Guid.NewGuid();
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    public Exception? ReportingFailure { get; private set; }

    public AutomaticBackupScheduler(CatalogRepository repository,
        Func<CancellationToken, Task<IReadOnlyList<MemberOperationResult>>> readRuntime,
        Func<ServerDefinition, AutomaticBackupService> createService,
        Func<Guid?, string, Task> report, TimeProvider? time = null)
        : this(repository, readRuntime, async (server, observation, now, ct) =>
        {
            var service = createService(server);
            if (observation is not null) await service.TickMeasuredAsync(observation, now, ct);
            else await service.TickAsync(TimeSpan.Zero, false, now, ct);
        }, report, time)
    { ArgumentNullException.ThrowIfNull(createService); }

    public AutomaticBackupScheduler(CatalogRepository repository,
        Func<CancellationToken, Task<IReadOnlyList<MemberOperationResult>>> readRuntime,
        Func<ServerDefinition, RunningTimeObservation?, DateTimeOffset, CancellationToken, Task> executeTick,
        Func<Guid?, string, Task> report, TimeProvider? time = null,
        Func<Guid, TimeSpan>? readClusterRuntime = null,
        Func<CatalogDocument, ClusterDefinition, RunningTimeObservation, DateTimeOffset, CancellationToken, Task>? executeClusterTick = null,
        Func<CancellationToken, Task>? maintenance = null)
    {
        ArgumentNullException.ThrowIfNull(repository); ArgumentNullException.ThrowIfNull(readRuntime);
        ArgumentNullException.ThrowIfNull(executeTick); ArgumentNullException.ThrowIfNull(report);
        this.repository = repository; this.readRuntime = readRuntime; this.executeTick = executeTick;
        this.report = report; this.time = time ?? TimeProvider.System;
        if ((readClusterRuntime is null) != (executeClusterTick is null))
            throw new ArgumentException("Cluster backup scheduling requires both its measured clock and work callback.");
        this.readClusterRuntime = readClusterRuntime; this.executeClusterTick = executeClusterTick;
        this.maintenance = maintenance;
    }

    public async Task RunCycleAsync(CancellationToken ct = default)
    {
        await cycleGate.WaitAsync(ct);
        try
        {
            if (maintenance is not null)
            {
                try { await maintenance(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                    or InvalidOperationException or ArgumentException or NotSupportedException or TimeoutException or System.Text.Json.JsonException)
                { await ReportAsync(null, "Temporary backup storage cleanup needs attention: " + error.Message); }
            }
            var catalog = await repository.ReadAsync(ct);
            var status = await readRuntime(ct);
            var now = time.GetUtcNow();
            // A slow or failed member must not prevent independent members from reaching their due work.
            await Parallel.ForEachAsync(catalog.Servers, new ParallelOptions
            { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (server, token) =>
            {
                try
                {
                    var measured = status.SingleOrDefault(s => s.ServerId == server.Id);
                    RunningTimeObservation? observation = measured?.Managed == true && measured.RunningTicks is long ticks
                        ? new(session, ticks) : null;
                    // No owned runtime means no inferred running time. Persisted retries still get a chance.
                    await executeTick(server, observation, now, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                    or InvalidOperationException or ArgumentException or NotSupportedException or TimeoutException or System.Text.Json.JsonException)
                { await ReportAsync(server.Id, "Automatic backup scheduling needs attention: " + error.Message); }
            });
            if (executeClusterTick is not null)
            {
                await Parallel.ForEachAsync(catalog.Clusters, new ParallelOptions
                { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (cluster, token) =>
                {
                    try
                    {
                        var ticks = readClusterRuntime!(cluster.Id).Ticks;
                        if (ticks < 0) throw new InvalidDataException("Measured cluster runtime must not be negative.");
                        // Keep stopped-cluster retries eligible without inventing elapsed running time.
                        await executeClusterTick(catalog, cluster, new(session, ticks), now, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                        or InvalidOperationException or ArgumentException or NotSupportedException or TimeoutException or System.Text.Json.JsonException)
                    { await ReportAsync(null, $"Automatic backup scheduling for cluster '{cluster.Name}' needs attention: " + error.Message); }
                });
            }
        }
        finally { cycleGate.Release(); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { await RunCycleAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException or NotSupportedException or TimeoutException or System.Text.Json.JsonException)
            { await ReportAsync(null, "Automatic backup schedule could not refresh: " + error.Message); }
            var now = time.GetUtcNow();
            var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddMinutes(1);
            await Task.Delay(next - now, time, ct);
        }
    }

    private async Task ReportAsync(Guid? serverId, string message)
    {
        try { await report(serverId, message); }
        catch (Exception error) { ReportingFailure = error; }
    }
}
