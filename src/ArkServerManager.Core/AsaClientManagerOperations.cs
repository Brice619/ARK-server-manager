using System.Text.Json;
namespace ArkServerManager.Core;
public sealed record AsaClientEdit(Guid ServerId, string Directory, AsaClientPlatforms Expected, AsaClientPlatforms Desired, bool Confirmed);
public sealed record AsaClientEditResult(Guid ServerId, AsaClientPlatforms Clients, bool AppliesOnNextStart);
public sealed class AsaClientManagerOperations(CatalogRepository repository, BackgroundOperations operations, ServerLifecycleCoordinator lifecycle)
{
    public BackgroundOperation Start(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<AsaClientEdit>() ?? throw new ArgumentException("Review player platforms.");
        if (!edit.Confirmed || edit.ServerId == Guid.Empty) throw new ArgumentException("Confirm player platforms.");
        AsaClientPlatformArguments.Build(edit.Expected); AsaClientPlatformArguments.Build(edit.Desired);
        return operations.Submit(request.Id, "asa-client-access-save", request.Payload, async (progress, ct) =>
        {
            await lifecycle.WithStatusScopeAsync(async (_, token) =>
            {
                progress(new("Saving player platforms for the next start."));
                await repository.ChangeAsync(catalog =>
                {
                    var server = catalog.Servers.Single(server => server.Id == edit.ServerId);
                    if (server.Directory != edit.Directory) throw new InvalidOperationException("The server installation changed. Reload before saving.");
                    catalog.UpdateAsaClients(edit.ServerId, edit.Expected, edit.Desired);
                }, token);
            }, ct);
            return new AsaClientEditResult(edit.ServerId, edit.Desired, true);
        });
    }
}
