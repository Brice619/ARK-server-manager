using System.Text.Json;
namespace ArkServerManager.Core;
public sealed record AseClientEdit(Guid ServerId, string Directory, AseClientAccess Expected, AseClientAccess Desired, bool Confirmed);
public sealed record AseClientEditResult(Guid ServerId, AseClientAccess Clients, bool AppliesOnNextStart);
public sealed class AseClientManagerOperations(CatalogRepository repository, BackgroundOperations operations, ServerLifecycleCoordinator lifecycle)
{
    public BackgroundOperation Start(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<AseClientEdit>() ?? throw new ArgumentException("Review player client access.");
        if (!edit.Confirmed || edit.ServerId == Guid.Empty || !Enum.IsDefined(edit.Desired) || !Enum.IsDefined(edit.Expected))
            throw new ArgumentException("Confirm supported player client access.");
        return operations.Submit(request.Id, "ase-client-access-save", request.Payload, async (progress, ct) =>
        {
            await lifecycle.WithStatusScopeAsync(async (_, token) =>
            {
                progress(new("Saving player client access for the next start."));
                await repository.ChangeAsync(catalog =>
                {
                    var server = catalog.Servers.Single(server => server.Id == edit.ServerId);
                    if (server.Directory != edit.Directory) throw new InvalidOperationException("The server installation changed. Reload before saving.");
                    catalog.UpdateAseClients(edit.ServerId, edit.Expected, edit.Desired);
                }, token);
            }, ct);
            return new AseClientEditResult(edit.ServerId, edit.Desired, true);
        });
    }
}
