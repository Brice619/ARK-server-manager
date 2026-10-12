using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ModOrderEdit(ServerDefinition Expected, List<string> Order, SaveChoice Choice, bool Confirmed, bool Replace = false);
public sealed record ModOrderEditResult(Guid ServerId, SettingsSaveResult Save);

public sealed class ModOrderManagerOperations(CatalogRepository repository, BackgroundOperations operations,
    ModOrderWorkflow workflow, Func<ServerDefinition, CancellationToken, Task<bool>> isRunning,
    Func<string, CancellationToken, Task<ModMetadata>>? lookupAse = null)
{
    public BackgroundOperation Start(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<ModOrderEdit>() ?? throw new ArgumentException("Review the mod order.");
        if (!edit.Confirmed || edit.Expected is null || edit.Order is null || edit.Expected.Id == Guid.Empty
            || !Enum.IsDefined(edit.Choice) || edit.Choice == SaveChoice.Cancel) throw new ArgumentException("Confirm the mod order save action.");
        AsaModLaunchArguments.Build(edit.Order);
        if (edit.Replace && edit.Expected.Edition == ArkEdition.ASE && lookupAse is null)
            throw new NotSupportedException("Mod addition/removal is not configured for this edition yet.");
        return operations.Submit(request.Id, "server-mod-order-save", request.Payload, async (progress, ct) =>
        {
            var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == edit.Expected.Id);
            if (server.Directory != edit.Expected.Directory || server.Edition != edit.Expected.Edition || server.Map != edit.Expected.Map
                || server.ClusterId != edit.Expected.ClusterId || !server.Mods.SequenceEqual(edit.Expected.Mods))
                throw new InvalidOperationException("The reviewed mod list changed. Refresh before saving.");
            progress(new("Saving the selected server's mod order."));
            if (edit.Replace && server.Edition == ArkEdition.ASE)
                foreach (var id in edit.Order.Except(server.Mods))
                {
                    var mod = await lookupAse!(id, ct);
                    if (mod.Edition != ArkEdition.ASE || mod.Id != ulong.Parse(id).ToString(System.Globalization.CultureInfo.InvariantCulture))
                        throw new InvalidDataException("The selected Workshop item could not be verified for ASE.");
                }
            var running = await isRunning(server, ct);
            return new ModOrderEditResult(server.Id, await workflow.SaveAsync(server.Id, edit.Order, running, edit.Choice, ct, edit.Replace));
        });
    }
}
