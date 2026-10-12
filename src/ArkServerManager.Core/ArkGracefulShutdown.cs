namespace ArkServerManager.Core;

/// <summary>Requests save and exit; the edition adapter must confirm that the world save completed.</summary>
public sealed class ArkGracefulShutdown
{
    private readonly Func<RconEndpoint, string, CancellationToken, Task<string>> execute;
    public ArkGracefulShutdown(Func<RconEndpoint, string, CancellationToken, Task<string>>? execute = null)
        => this.execute = execute ?? new RconClient().ExecuteAsync;

    /// <summary>The edition adapter supplies its verified save marker; checkpoint before issuing SaveWorld.</summary>
    public Task RequestWithConsoleConfirmationAsync(ManagedProcess process, RconEndpoint endpoint,
        Func<ProcessLine, bool> confirmsSave, TimeSpan saveTimeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(confirmsSave);
        if (saveTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(saveTimeout));
        var checkpoint = process.ConsoleCheckpoint;
        return RequestAsync(() => process.IsAlive, endpoint,
            async token => { await process.WaitForConsoleAsync(checkpoint, confirmsSave, saveTimeout, token); }, ct);
    }

    public async Task RequestAsync(Func<bool> isAlive, RconEndpoint endpoint,
        Func<CancellationToken, Task> confirmSaved, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(isAlive);
        ArgumentNullException.ThrowIfNull(confirmSaved);
        ct.ThrowIfCancellationRequested();
        if (!isAlive()) return;
        await execute(endpoint, "SaveWorld", ct);
        await confirmSaved(ct);
        ct.ThrowIfCancellationRequested();
        if (!isAlive()) throw new IOException("The server exited before its confirmed save could be followed by shutdown.");
        await execute(endpoint, "DoExit", ct);
        // Successful command response is not proof of exit; ServerSupervisor waits for its owned process.
    }

    /// <summary>Requires an explicit edition-specific save acknowledgement before requesting exit.</summary>
    public async Task RequestWithResponseConfirmationAsync(ManagedProcess process, RconEndpoint endpoint,
        Func<string, bool> confirmsSave, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(process); ArgumentNullException.ThrowIfNull(confirmsSave);
        ct.ThrowIfCancellationRequested();
        if (!process.IsAlive) return;
        var response = await execute(endpoint, "SaveWorld", ct);
        if (!confirmsSave(response))
            throw new IOException("The server did not confirm its world save. It was left running.");
        ct.ThrowIfCancellationRequested();
        if (!process.IsAlive) throw new IOException("The server exited before its confirmed save could be followed by shutdown.");
        await execute(endpoint, "DoExit", ct);
    }
}
