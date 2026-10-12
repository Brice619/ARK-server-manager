using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ServerUpdateCheckRequest(Guid ServerId);
public sealed record ServerUpdateCheckResult(Guid ServerId, ulong InstalledBuild, ulong AvailableBuild, ServerUpdateResult Result);

public sealed class ServerUpdateManagerOperations(CatalogRepository repository, BackgroundOperations operations,
    Func<ServerDefinition, CancellationToken, Task<SteamBuild>> query,
    Func<ServerDefinition, SteamBuild, CancellationToken, Task<ServerUpdateCheckResult>> apply)
{
    public BackgroundOperation Start(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<ServerUpdateCheckRequest>() ?? throw new ArgumentException("Choose a server.");
        if (selection.ServerId == Guid.Empty) throw new ArgumentException("Choose a registered server.");
        return operations.Submit(request.Id, "server-update-check", request.Payload, async (progress, ct) =>
            await CheckAsync(selection.ServerId, progress, ct));
    }

    public async Task<ServerUpdateCheckResult> CheckAsync(Guid serverId, Action<OperationProgress> progress, CancellationToken ct)
    {
            var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == serverId);
            progress(new("Checking the available server version."));
            var available = await query(server, ct);
            if (available.AppId != SteamCmdInstaller.AppId(server.Edition)) throw new InvalidDataException("The update belongs to another game.");
            progress(new("Checking server state before applying or queuing the update."));
            return await apply(server, available, ct);
    }

    public static async Task<ulong> InstalledBuildAsync(ServerDefinition server, CancellationToken ct)
    {
        var path = Path.Combine(server.Directory, "steamapps", "appmanifest_" + SteamCmdInstaller.AppId(server.Edition) + ".acf");
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked installation manifests require separate validation.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 1024 * 1024) throw new InvalidDataException("The installation manifest exceeds the size limit.");
        using var reader = new StreamReader(input);
        return SteamBuildMetadata.ReadInstalled(await reader.ReadToEndAsync(ct), SteamCmdInstaller.AppId(server.Edition));
    }
}
