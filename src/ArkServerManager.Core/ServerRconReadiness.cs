using System.Net.Sockets;

namespace ArkServerManager.Core;

/// <summary>Waits for an owned process and authenticated local administration response; does not prove gameplay readiness.</summary>
public sealed class ServerRconReadiness
{
    private readonly Func<RconEndpoint, string, CancellationToken, Task<string>> execute;
    public ServerRconReadiness(Func<RconEndpoint, string, CancellationToken, Task<string>>? execute = null)
        => this.execute = execute ?? new RconClient().ExecuteAsync;

    public async Task WaitAsync(Func<bool> isAlive, RconEndpoint endpoint, TimeSpan startupTimeout,
        TimeSpan retryInterval, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(isAlive);
        if (startupTimeout <= TimeSpan.Zero || retryInterval <= TimeSpan.Zero)
            throw new ArgumentException("Choose positive startup and retry durations.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(startupTimeout);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!isAlive()) throw new IOException("The owned server exited before administration became available.");
                try
                {
                    await execute(endpoint, "ListPlayers", deadline.Token);
                    if (!isAlive()) throw new IOException("The owned server exited during the readiness check.");
                    return;
                }
                catch (Exception error) when (error is SocketException or TimeoutException or IOException)
                {
                    if (!isAlive()) throw new IOException("The owned server exited before administration became available.");
                    // Unavailable connections may be transient during startup. Bad credentials/protocol fail immediately.
                }
                await Task.Delay(retryInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("The server did not provide a working administration connection before the startup deadline."); }
    }
}
