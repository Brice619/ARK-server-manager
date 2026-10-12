namespace ArkServerManager.Core;

public sealed record ServerIncident(DateTimeOffset Time, string Stage, string Message, int? ExitCode);

/// <summary>Supervises one member. The cluster coordinator expands user actions; recovery stays local.</summary>
public sealed class ServerSupervisor : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<ProcessLaunch>> launch;
    private readonly Func<ManagedProcess, CancellationToken, Task> waitUntilReady;
    private readonly Func<ManagedProcess, CancellationToken, Task> stopGracefully;
    private readonly Func<ServerIncident, Task> recordIncident;
    private readonly TimeSpan shutdownTimeout;
    private readonly Action<ServerState>? stateChanged;
    private readonly SemaphoreSlim? recoveryGate;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private ManagedProcess? current;
    private Task monitoring = Task.CompletedTask;
    private readonly RunningTimeTracker runningTime = new();
    private ServerState state
    {
        get => runningTime.State;
        set
        {
            runningTime.Transition(value);
            try { stateChanged?.Invoke(value); }
            catch (Exception error) { StateObservationFailure = error; }
        }
    }
    private int disposed;
    public ServerState State => state;
    public TimeSpan RunningTime => runningTime.Runtime;
    public Exception? IncidentRecordingFailure { get; private set; }
    public Exception? StateObservationFailure { get; private set; }
    public IReadOnlyList<ProcessLine> ConsoleTail => current?.ConsoleTail ?? [];

    public ServerSupervisor(Func<ProcessLaunch> launch,
        Func<ManagedProcess, CancellationToken, Task> waitUntilReady,
        Func<ManagedProcess, CancellationToken, Task> stopGracefully,
        Func<ServerIncident, Task> recordIncident, TimeSpan shutdownTimeout, Action<ServerState>? stateChanged = null, SemaphoreSlim? recoveryGate = null)
        : this(_ => Task.FromResult(launch()), waitUntilReady, stopGracefully, recordIncident, shutdownTimeout, stateChanged, recoveryGate) { }

    public ServerSupervisor(Func<CancellationToken, Task<ProcessLaunch>> launch,
        Func<ManagedProcess, CancellationToken, Task> waitUntilReady,
        Func<ManagedProcess, CancellationToken, Task> stopGracefully,
        Func<ServerIncident, Task> recordIncident, TimeSpan shutdownTimeout, Action<ServerState>? stateChanged = null, SemaphoreSlim? recoveryGate = null)
    {
        if (shutdownTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        this.launch = launch;
        this.waitUntilReady = waitUntilReady;
        this.stopGracefully = stopGracefully;
        this.recordIncident = recordIncident;
        this.shutdownTimeout = shutdownTimeout;
        this.stateChanged = stateChanged;
        this.recoveryGate = recoveryGate;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (state is not (ServerState.Stopped or ServerState.Failed)) throw new InvalidOperationException("The server is already active.");
            if (current is not null) { await current.DisposeAsync(); current = null; }
            state = ServerState.Starting;
            try
            {
                var prepared = await launch(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                current = ManagedProcess.Start(prepared);
                await waitUntilReady(current, cancellationToken);
                if (!current.IsAlive) throw new IOException("The server exited before becoming ready.");
                state = ServerState.Running;
                monitoring = MonitorAsync(current);
            }
            catch (Exception error)
            {
                state = ServerState.Failed;
                if (current is not null) { await current.DisposeAsync(); current = null; }
                await ReportAsync("startup", error.Message, null);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (current is null || !current.IsAlive) { state = ServerState.Stopped; return true; }
            state = ServerState.Stopping;
            try
            {
                if (!await current.RequestStopAsync(ct => stopGracefully(current, ct), shutdownTimeout, cancellationToken))
                {
                    state = ServerState.Running;
                    await ReportAsync("shutdown", "The server did not exit within the shutdown timeout; it was left running.", null);
                    return false;
                }
                state = ServerState.Stopped;
                return true;
            }
            catch { state = ServerState.Running; throw; }
        }
        finally { gate.Release(); }
    }

    private async Task MonitorAsync(ManagedProcess observed)
    {
        ProcessExit exit;
        try { exit = await observed.Completion; }
        catch (Exception error)
        {
            await ReportAsync("console", error.Message, null);
            exit = new(-1, DateTimeOffset.UtcNow, false);
        }
        // Use the same lock order as user lifecycle actions: file scope, then process scope.
        // Waiting for the file scope while holding the process scope would deadlock Stop.
        try { if (recoveryGate is not null) await recoveryGate.WaitAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        await gate.WaitAsync();
        try
        {
            if (current != observed || lifetime.IsCancellationRequested || state == ServerState.Stopped) return;
            if (exit.RequestedStop) { state = ServerState.Stopped; return; }
            state = ServerState.Recovering;
            await ReportAsync("runtime", "The server process exited unexpectedly.", exit.ExitCode);
            await observed.DisposeAsync();
            current = null;
            var recovery = new CrashRecovery();
            recovery.RuntimeCrashed();
            while (recovery.MayAttemptStartup && !lifetime.IsCancellationRequested)
            {
                try
                {
                    var prepared = await launch(lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    current = ManagedProcess.Start(prepared);
                    await waitUntilReady(current, lifetime.Token);
                    if (!current.IsAlive) throw new IOException("The restarted server exited before becoming ready.");
                    recovery.StartupSucceeded();
                    state = ServerState.Running;
                    // Yield avoids recursive synchronous monitoring of an already-exited process.
                    monitoring = MonitorNextAsync(current);
                    return;
                }
                catch (Exception error)
                {
                    int? code = current is not null && !current.IsAlive ? (await current.Completion).ExitCode : null;
                    if (current is not null) { await current.DisposeAsync(); current = null; }
                    recovery.StartupFailed();
                    await ReportAsync("recovery", error.Message, code);
                }
            }
            state = ServerState.Failed;
        }
        finally { gate.Release(); recoveryGate?.Release(); }
    }

    private async Task MonitorNextAsync(ManagedProcess process)
    {
        await Task.Yield();
        await MonitorAsync(process);
    }

    private async Task ReportAsync(string stage, string message, int? code)
    {
        try { await recordIncident(new(DateTimeOffset.UtcNow, stage, message, code)); }
        catch (Exception error) { IncidentRecordingFailure = error; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync();
        await gate.WaitAsync();
        try
        {
            state = ServerState.Stopped;
            if (current is not null) { await current.DisposeAsync(); current = null; }
        }
        finally { gate.Release(); }
        await monitoring;
        lifetime.Dispose();
    }
}
