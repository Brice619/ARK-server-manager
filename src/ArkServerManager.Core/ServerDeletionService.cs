namespace ArkServerManager.Core;

/// <summary>Coordinates exact-name deletion with member-only shutdown and locked stopped-file verification.</summary>
public sealed class ServerDeletionService(CatalogRepository repository, ServerLifecycleCoordinator lifecycle,
    Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped,
    Func<ServerDefinition, CancellationToken, Task>? beforeDelete = null,
    Func<ServerDeletionRecord, CancellationToken, Task>? afterDelete = null)
{
    public async Task<ServerDeletionRecord> DeleteAsync(Guid operationId, Guid serverId, string confirmedName,
        string managementRoot, IReadOnlyList<string> sharedPaths, CancellationToken ct = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A deletion identity is required.");
        var catalog = await repository.ReadAsync(ct);
        var selected = catalog.Servers.SingleOrDefault(s => s.Id == serverId)
            ?? throw new ArgumentException("Choose an existing server.");
        if (!string.Equals(selected.Settings.Name, confirmedName, StringComparison.Ordinal))
            throw new ArgumentException("Enter the exact server name before deletion.");
        // Reject invalid ownership/protected-file scope before stopping anything.
        ServerDeletionPlan.Create(catalog, serverId, managementRoot, sharedPaths);
        var stopped = await lifecycle.ExecuteMemberAsync(serverId, LifecycleAction.Stop, ct);
        if (!stopped.Success) throw new InvalidOperationException("The selected map did not stop. Its files have been left untouched.");
        var deleted = await ServerDeletionTransaction.ExecuteAsync(repository, operationId, serverId, confirmedName,
            managementRoot, sharedPaths,
            (server, change, token) => lifecycle.WithStoppedMemberAsync(server, async scopeToken =>
            {
                if (beforeDelete is not null) await beforeDelete(server, scopeToken);
                await change(scopeToken);
            }, verifyStopped, token), ct);
        await DeletedServerRecordCleanup.RemoveAsync(await repository.ReadAsync(ct), deleted, managementRoot, ct);
        if (afterDelete is not null) await afterDelete(deleted, ct);
        return deleted;
    }
}
