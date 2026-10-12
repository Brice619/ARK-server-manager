namespace ArkServerManager.Core;

/// <summary>Connects host lifecycle requests to member-local process supervision using fresh launch settings.</summary>
public sealed class SupervisedServerRuntime : IRunningTimeRuntime, IAsyncDisposable
{
    private readonly Guid serverId;
    private readonly Func<ServerDefinition, CancellationToken, Task<ServerSupervisor>> createSupervisor;
    public SupervisedServerRuntime(Guid serverId, Func<ServerDefinition, ServerSupervisor> createSupervisor)
        : this(serverId, (definition, _) => Task.FromResult(createSupervisor(definition))) { }

    public SupervisedServerRuntime(Guid serverId,
        Func<ServerDefinition, CancellationToken, Task<ServerSupervisor>> createSupervisor)
    {
        ArgumentNullException.ThrowIfNull(createSupervisor);
        this.serverId = serverId; this.createSupervisor = createSupervisor;
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    private ServerSupervisor? supervisor;
    private readonly object runtimeClockGate = new();
    private TimeSpan retiredRunningTime;
    public TimeSpan RunningTime
    {
        get { lock (runtimeClockGate) return retiredRunningTime + (supervisor?.RunningTime ?? TimeSpan.Zero); }
    }
    private bool disposed;
    public ServerState State => supervisor?.State ?? ServerState.Stopped;
    public IReadOnlyList<ProcessLine> ConsoleTail => supervisor?.ConsoleTail ?? [];

    public async Task StartAsync(ServerDefinition definition, CancellationToken ct)
    {
        if (definition.Id != serverId) throw new ArgumentException("The runtime belongs to a different server.");
        definition.Settings.Validate();
        var snapshot = definition with { Settings = definition.Settings.Copy(), Mods = [.. definition.Mods] };
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (State is not (ServerState.Stopped or ServerState.Failed)) throw new InvalidOperationException("The server is already active.");
            if (supervisor is not null)
            {
                await supervisor.DisposeAsync();
                lock (runtimeClockGate) { retiredRunningTime += supervisor.RunningTime; supervisor = null; }
            }
            var created = await createSupervisor(snapshot, ct);
            lock (runtimeClockGate) supervisor = created;
            await supervisor.StartAsync(ct);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> StopAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return supervisor is null || await supervisor.StopAsync(ct);
        }
        finally { gate.Release(); }
    }

    // Resource disposal terminates owned children. Normal host shutdown must request graceful stops first.
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            if (supervisor is not null) await supervisor.DisposeAsync();
        }
        finally { gate.Release(); }
    }
}
