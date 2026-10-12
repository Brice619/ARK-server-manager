using System.Text.Json;

namespace ArkServerManager.Core;
public sealed record ClusterDefaultsReview(Guid ClusterId, string Name, ArkEdition Edition, int MaxPlayers);
public sealed record ClusterDefaultsEdit(ClusterDefaultsReview Expected, string Name, int MaxPlayers, bool Confirmed);
public sealed class ClusterDefaultsManagerOperations(CatalogRepository repository, BackgroundOperations operations,
    Func<Func<CancellationToken, Task>, CancellationToken, Task>? withPublicationScope = null,
    Func<Guid, CancellationToken, Task>? requireDefaultsAllowed = null)
{
    public async Task<ClusterDefaultsReview> ReadAsync(Guid id, CancellationToken ct = default)
    {
        var cluster = (await repository.ReadAsync(ct)).Clusters.Single(c => c.Id == id);
        return new(cluster.Id, cluster.Name, cluster.Edition, cluster.Settings.MaxPlayers);
    }
    public BackgroundOperation Start(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<ClusterDefaultsEdit>() ?? throw new ArgumentException("Review cluster defaults.");
        if (!edit.Confirmed || edit.Expected is null || edit.Expected.ClusterId == Guid.Empty)
            throw new ArgumentException("Confirm the cluster default changes.");
        return operations.Submit(request.Id, "cluster-defaults-save", request.Payload, async (progress, ct) =>
        {
            progress(new("Saving cluster defaults for new maps."));
            CatalogDocument? saved = null;
            async Task Publish(CancellationToken token)
            {
            if (requireDefaultsAllowed is not null) await requireDefaultsAllowed(edit.Expected.ClusterId, token);
            saved = await repository.ChangeAsync(catalog =>
            {
                var current = catalog.Clusters.Single(c => c.Id == edit.Expected.ClusterId);
                if (current.Name != edit.Expected.Name || current.Edition != edit.Expected.Edition || current.Settings.MaxPlayers != edit.Expected.MaxPlayers)
                    throw new InvalidOperationException("Cluster defaults changed. Reload them before saving.");
                catalog.UpdateClusterDefaults(current.Id, edit.Name, edit.MaxPlayers);
            }, token);
            }
            if (withPublicationScope is null) await Publish(ct); else await withPublicationScope(Publish, ct);
            var result = saved!.Clusters.Single(c => c.Id == edit.Expected.ClusterId);
            return new ClusterDefaultsReview(result.Id, result.Name, result.Edition, result.Settings.MaxPlayers);
        });
    }
}
