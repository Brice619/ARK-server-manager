using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ServerDeletionSelection(Guid ServerId, string ConfirmedName);

/// <summary>Protected cluster locations come from host metadata, never from a desktop deletion payload.</summary>
public sealed class ServerDeletionManagerOperations(CatalogRepository repository, ServerDeletionService service,
    BackgroundOperations operations, string managementRoot,
    Func<ServerDefinition, CancellationToken, Task<IReadOnlyList<string>>> sharedPaths)
{
    public BackgroundOperation Start(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<ServerDeletionSelection>()
            ?? throw new ArgumentException("Review the selected server before deletion.");
        if (selection.ServerId == Guid.Empty || string.IsNullOrWhiteSpace(selection.ConfirmedName))
            throw new ArgumentException("Enter the exact server name before deletion.");
        return operations.Submit(request.Id, "server-deletion", request.Payload, async (progress, ct) =>
        {
            var catalog = await repository.ReadAsync(ct);
            var server = catalog.Servers.SingleOrDefault(s => s.Id == selection.ServerId)
                ?? throw new ArgumentException("The selected server is unavailable.");
            if (server.Settings.Name != selection.ConfirmedName)
                throw new ArgumentException("Enter the exact server name before deletion.");
            var protectedPaths = await sharedPaths(server, ct);
            progress(new("Checking files and stopping only the selected map before deletion."));
            return await service.DeleteAsync(request.Id, server.Id, selection.ConfirmedName,
                managementRoot, protectedPaths, ct);
        });
    }
}
