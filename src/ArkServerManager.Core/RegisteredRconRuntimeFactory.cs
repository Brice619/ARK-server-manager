namespace ArkServerManager.Core;

/// <summary>Composes actual registered credentials, owned-process readiness and recovery with a validated launch/shutdown adapter.</summary>
public sealed class RegisteredRconRuntimeFactory
{
    private readonly CatalogRepository repository;
    private readonly Func<ServerDefinition, CancellationToken, Task<ProcessLaunch>> buildLaunch;
    private readonly Func<ManagedProcess, RconEndpoint, CancellationToken, Task> shutdown;
    private readonly Func<Guid, ServerIncident, Task> recordIncident;
    private readonly TimeSpan connectionTimeout, startupTimeout, retryInterval, shutdownTimeout;
    private readonly RconResponseMode responseMode;
    private readonly Action<Guid, Guid?, ServerState>? observeState;
    private readonly SemaphoreSlim? recoveryGate;
    private readonly Func<ServerDefinition, CancellationToken, Task>? verifyReady;

    public RegisteredRconRuntimeFactory(CatalogRepository repository,
        Func<ServerDefinition, ProcessLaunch> buildLaunch,
        Func<ManagedProcess, RconEndpoint, CancellationToken, Task> shutdown,
        Func<Guid, ServerIncident, Task> recordIncident,
        TimeSpan connectionTimeout, TimeSpan startupTimeout, TimeSpan retryInterval, TimeSpan shutdownTimeout,
        RconResponseMode responseMode, Action<Guid, Guid?, ServerState>? observeState = null, SemaphoreSlim? recoveryGate = null,
        Func<ServerDefinition, CancellationToken, Task>? verifyReady = null)
        : this(repository, WrapLaunch(buildLaunch), shutdown, recordIncident, connectionTimeout,
            startupTimeout, retryInterval, shutdownTimeout, responseMode, observeState, recoveryGate, verifyReady) { }

    public RegisteredRconRuntimeFactory(CatalogRepository repository,
        Func<ServerDefinition, CancellationToken, Task<ProcessLaunch>> buildLaunch,
        Func<ManagedProcess, RconEndpoint, CancellationToken, Task> shutdown,
        Func<Guid, ServerIncident, Task> recordIncident,
        TimeSpan connectionTimeout, TimeSpan startupTimeout, TimeSpan retryInterval, TimeSpan shutdownTimeout,
        RconResponseMode responseMode, Action<Guid, Guid?, ServerState>? observeState = null, SemaphoreSlim? recoveryGate = null,
        Func<ServerDefinition, CancellationToken, Task>? verifyReady = null)
    {
        ArgumentNullException.ThrowIfNull(repository); ArgumentNullException.ThrowIfNull(buildLaunch);
        ArgumentNullException.ThrowIfNull(shutdown); ArgumentNullException.ThrowIfNull(recordIncident);
        if (new[] { connectionTimeout, startupTimeout, retryInterval, shutdownTimeout }.Any(t => t <= TimeSpan.Zero)
            || !Enum.IsDefined(responseMode)) throw new ArgumentException("Choose valid runtime timing and response options.");
        this.repository = repository; this.buildLaunch = buildLaunch; this.shutdown = shutdown;
        this.recordIncident = recordIncident; this.connectionTimeout = connectionTimeout;
        this.startupTimeout = startupTimeout; this.retryInterval = retryInterval;
        this.shutdownTimeout = shutdownTimeout; this.responseMode = responseMode;
        this.observeState = observeState;
        this.recoveryGate = recoveryGate;
        this.verifyReady = verifyReady;
    }

    public SupervisedServerRuntime Create(Guid serverId) => new(serverId, (definition, ct) =>
    {
        RconEndpoint endpoint = null!;
        ServerDefinition prepared = definition;
        var readiness = new ServerRconReadiness();
        return Task.FromResult(new ServerSupervisor(async token =>
            {
                var current = (await repository.ReadAsync(token)).Servers.SingleOrDefault(s => s.Id == definition.Id);
                if (current is null || current.Directory != definition.Directory || current.Edition != definition.Edition
                    || current.ClusterId != definition.ClusterId)
                    throw new InvalidOperationException("The selected installation changed before launch.");
                // Recovery must prepare a fresh launch and credentials rather than reuse the crashed process's plan.
                var launch = await buildLaunch(current, token);
                prepared = (await repository.ReadAsync(token)).Servers.Single(s => s.Id == current.Id);
                if (prepared.Directory != current.Directory || prepared.Edition != current.Edition || prepared.ClusterId != current.ClusterId)
                    throw new InvalidOperationException("The selected installation changed during launch preparation.");
                endpoint = await ServerRconConfiguration.ReadAsync(repository, current.Id, connectionTimeout, responseMode, token);
                token.ThrowIfCancellationRequested();
                return launch with { Secrets = [.. launch.Secrets ?? [], endpoint.Password] };
            },
            async (process, token) =>
            {
                await readiness.WaitAsync(() => process.IsAlive, endpoint, startupTimeout, retryInterval, token);
                if (verifyReady is not null) await verifyReady(prepared, token);
                if (!process.IsAlive) throw new IOException("The owned server exited during startup validation.");
            },
            (process, token) => shutdown(process, endpoint, token),
            incident => recordIncident(definition.Id, incident), shutdownTimeout,
            state => observeState?.Invoke(definition.Id, definition.ClusterId, state), recoveryGate));
    });

    private static Func<ServerDefinition, CancellationToken, Task<ProcessLaunch>> WrapLaunch(
        Func<ServerDefinition, ProcessLaunch> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return (definition, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(build(definition));
        };
    }
}
