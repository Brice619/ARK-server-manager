using System.Text.Json;
namespace ArkServerManager.Core;
public sealed record RouterForwardingSelection(Guid ServerId, string Directory, int GamePort, int QueryPort, bool Confirmed);
public enum RouterSetupPhase { Preparing, Applying, Completed, NeedsReview, Removed }
public sealed record RouterRemovalSelection(Guid ServerId, Guid ExpectedOperationId, bool Confirmed);
public sealed record RouterSetupStatus(Guid ServerId, RouterSetupRecord? Record);
public sealed record RouterSetupRecord(int SchemaVersion, Guid OperationId, Guid ServerId, string Directory, string Gateway,
    int GamePort, int QueryPort, RouterSetupPhase Phase, int? AttemptedPort, List<int> VerifiedPorts);
public sealed class RouterManagerOperations(string managementRoot, CatalogRepository repository, BackgroundOperations operations, ServerLifecycleCoordinator lifecycle)
{
    public async Task<RouterSetupStatus> StatusAsync(Guid id, CancellationToken ct = default) => new(id, await ReadAsync(id, ct));
    private AtomicJsonStore<RouterSetupRecord> Store(Guid id) => new(Path.Combine(managementRoot, "app-settings", "router-" + id.ToString("N") + ".json"));
    public async Task<RouterSetupRecord?> ReadAsync(Guid id, CancellationToken ct = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Choose a server.");
        var record = await Store(id).LoadAsync(ct);
        if (record is not null && (record.SchemaVersion != 1 || record.ServerId != id || record.OperationId == Guid.Empty
            || !Enum.IsDefined(record.Phase) || record.VerifiedPorts is null || record.VerifiedPorts.Count > 3
            || record.GamePort is < 1 or > 65534 || record.QueryPort is < 1 or > 65535
            || new[] { record.GamePort, record.GamePort + 1, record.QueryPort }.Distinct().Count() != 3
            || !Path.IsPathFullyQualified(record.Directory) || !System.Net.IPAddress.TryParse(record.Gateway, out var gateway)
            || !LocalNetworkAddresses.UsableIPv4(gateway)
            || record.VerifiedPorts.Any(port => port != record.GamePort && port != record.GamePort + 1 && port != record.QueryPort)))
            throw new InvalidDataException("Router setup record needs review.");
        return record;
    }
    public BackgroundOperation Start(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<RouterForwardingSelection>() ?? throw new ArgumentException("Review router forwarding.");
        if (!selection.Confirmed || selection.ServerId == Guid.Empty) throw new ArgumentException("Confirm router forwarding.");
        return operations.Submit(request.Id, "router-forwarding-setup", request.Payload, async (progress, ct) =>
        {
            RouterForwardingResult? result = null;
            await lifecycle.WithStatusScopeAsync(async (_, token) =>
            {
                var server = (await repository.ReadAsync(token)).Servers.Single(server => server.Id == selection.ServerId);
                if (server.Directory != selection.Directory || server.Settings.GamePort != selection.GamePort || server.Settings.QueryPort != selection.QueryPort)
                    throw new InvalidOperationException("Server network settings changed. Review forwarding again.");
                if (await ReadAsync(server.Id, token) is { Phase: not RouterSetupPhase.Removed })
                    throw new InvalidOperationException("Router setup is already recorded. Review its rules before another setup.");
                progress(new("Looking for a supported router."));
                var router = await RouterDiscovery.FindAsync(token);
                var record = new RouterSetupRecord(1, request.Id, server.Id, server.Directory, router.DeviceEndpoint.Address.ToString(),
                    server.Settings.GamePort, server.Settings.QueryPort, RouterSetupPhase.Preparing, null, []);
                var store = Store(server.Id);
                await store.SaveAsync(record, token);
                try
                {
                    result = await RouterPortForwarding.CreateAsync(router, server, progress, token, async (port, verified) =>
                    {
                        record = record with { Phase = RouterSetupPhase.Applying, AttemptedPort = port,
                            VerifiedPorts = verified ? [.. record.VerifiedPorts, port] : record.VerifiedPorts };
                        await store.SaveAsync(record, CancellationToken.None);
                    });
                    await store.SaveAsync(record with { Phase = RouterSetupPhase.Completed, AttemptedPort = null }, CancellationToken.None);
                }
                catch
                {
                    await store.SaveAsync(record with { Phase = RouterSetupPhase.NeedsReview }, CancellationToken.None);
                    throw;
                }
            }, ct);
            return result!;
        });
    }

    public BackgroundOperation Remove(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<RouterRemovalSelection>() ?? throw new ArgumentException("Review router rule removal.");
        if (!selection.Confirmed || selection.ServerId == Guid.Empty || selection.ExpectedOperationId == Guid.Empty)
            throw new ArgumentException("Confirm the recorded router rules.");
        return operations.Submit(request.Id, "router-forwarding-remove", request.Payload, async (progress, ct) =>
        {
            RouterSetupRecord? result = null;
            await lifecycle.WithStatusScopeAsync(async (_, token) =>
            {
                var record = await ReadAsync(selection.ServerId, token) ?? throw new InvalidOperationException("No router setup is recorded.");
                if (record.OperationId != selection.ExpectedOperationId) throw new InvalidOperationException("Router setup changed. Reload before removing rules.");
                if (record.Phase == RouterSetupPhase.Removed) { result = record; return; }
                progress(new("Looking for the recorded router and removing this app’s player rules."));
                var router = await RouterDiscovery.FindAsync(token);
                try
                {
                    await RouterPortForwarding.RemoveAsync(router, record, token);
                    result = record with { Phase = RouterSetupPhase.Removed, AttemptedPort = null, VerifiedPorts = [] };
                    await Store(record.ServerId).SaveAsync(result, CancellationToken.None);
                }
                catch
                {
                    await Store(record.ServerId).SaveAsync(record with { Phase = RouterSetupPhase.NeedsReview }, CancellationToken.None);
                    throw;
                }
            }, ct);
            return new RouterSetupStatus(selection.ServerId, result);
        });
    }

    /// <summary>Caller holds the stopped-map lifecycle scope during deletion.</summary>
    public async Task RemoveForDeletionAsync(ServerDefinition server, CancellationToken ct)
    {
        var record = await ReadAsync(server.Id, ct);
        if (record is null || record.Phase == RouterSetupPhase.Removed) return;
        if (record.Directory != server.Directory) throw new InvalidOperationException("Recorded router setup belongs to an earlier server location. Review it before deleting.");
        var router = await RouterDiscovery.FindAsync(ct);
        try
        {
            await RouterPortForwarding.RemoveAsync(router, record, ct);
            await Store(server.Id).SaveAsync(record with { Phase = RouterSetupPhase.Removed, AttemptedPort = null, VerifiedPorts = [] }, CancellationToken.None);
        }
        catch
        {
            await Store(server.Id).SaveAsync(record with { Phase = RouterSetupPhase.NeedsReview }, CancellationToken.None);
            throw;
        }
    }
}
