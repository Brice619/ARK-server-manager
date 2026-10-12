namespace ArkServerManager.Core;
/// <summary>Caller holds the verified-stopped lifecycle scope. Package repair supplies bundled runtimes.</summary>
public sealed class ServerRuntimePreparation(Func<ServerDefinition, IReadOnlyList<ServerRuntimeLibrary>> inspect,
    Func<ServerDefinition, CancellationToken, Task<bool>> repair)
{
    public async Task PrepareAsync(ServerDefinition server, CancellationToken ct)
    {
        var before = Read();
        if (before.All(library => library.Ready)) return;
        if (!await repair(server, ct)) throw new IOException("Required server-runtime files could not be repaired. The server remains stopped.");
        var after = Read();
        if (after.Any(library => !library.Ready))
            throw new IOException("Server-runtime validation still needs attention: " + string.Join(", ", after.Where(library => !library.Ready).Select(library => library.Name)));
        IReadOnlyList<ServerRuntimeLibrary> Read()
        {
            try { return inspect(server); }
            catch (Exception error) when (error is IOException or BadImageFormatException or UnauthorizedAccessException)
            { return [new("server executable", server.Directory, false, "The executable could not be inspected.")]; }
        }
    }
}
