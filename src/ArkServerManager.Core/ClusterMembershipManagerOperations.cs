using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ClusterMemberReview(Guid ServerId, ArkEdition Edition, string Directory, Guid? ClusterId);
public sealed record ClusterMembershipEdit(List<ClusterMemberReview> Expected, Guid? TargetClusterId, bool Confirmed)
{
    public string? NewClusterName { get; init; }
}

/// <summary>Confirmed standalone-to-cluster joining applies inherited defaults through a recoverable transaction.</summary>
public sealed class ClusterMembershipManagerOperations(CatalogRepository repository, BackgroundOperations operations,
    ServerLifecycleCoordinator lifecycle, InstallationStopGuard stopGuard, ClusterJoiningTransaction joining)
{
    public async Task<IReadOnlyList<ClusterMemberReview>> ReadAsync(CancellationToken ct = default)
        => (await repository.ReadAsync(ct)).Servers.Select(Review).ToArray();

    private static ClusterMemberReview Review(ServerDefinition server)
        => new(server.Id, server.Edition, server.Directory, server.ClusterId);

    public BackgroundOperation Start(ManagerRequest request)
    {
        ArgumentNullException.ThrowIfNull(joining);
        var edit = request.Payload?.Deserialize<ClusterMembershipEdit>() ?? throw new ArgumentException("Review cluster membership.");
        if (!edit.Confirmed || edit.Expected is null || edit.Expected.Count is < 1 or > 1024
            || edit.Expected.Any(member => member is null || member.ServerId == Guid.Empty)
            || edit.Expected.Select(member => member.ServerId).Distinct().Count() != edit.Expected.Count)
            throw new ArgumentException("Confirm a distinct selection of servers.");
        if (edit.TargetClusterId is null || edit.TargetClusterId == Guid.Empty || edit.Expected.Any(member => member.ClusterId is not null))
            throw new ArgumentException("Only standalone servers can be added to a cluster. Detaching maps and moving them between clusters are outside this app's scope.");
        if (edit.NewClusterName is not null && (edit.TargetClusterId is null || edit.TargetClusterId == Guid.Empty
            || string.IsNullOrWhiteSpace(edit.NewClusterName) || edit.NewClusterName.Length > 256 || edit.NewClusterName.Any(char.IsControl)))
            throw new ArgumentException("Enter a name for the new cluster.");
        return operations.Submit(request.Id, "cluster-membership-save", request.Payload, async (progress, ct) =>
        {
            CatalogDocument? saved = null;
            await lifecycle.WithStatusScopeAsync(async (statuses, token) =>
            {
                progress(new("Checking affected servers before changing their cluster."));
                if (joining is not null)
                {
                    var document = await repository.ReadAsync(token);
                    foreach (var expected in edit.Expected)
                        if (document.Servers.SingleOrDefault(s => s.Id == expected.ServerId) is not { } selected || Review(selected) != expected)
                            throw new InvalidOperationException("Server membership changed. Reload before saving.");
                    var selectedServers = edit.Expected.Select(e => document.Servers.Single(s => s.Id == e.ServerId)).ToArray();
                    ClusterDefinition target;
                    if (edit.NewClusterName is { } name)
                    {
                        if (document.Clusters.Any(c => c.Id == edit.TargetClusterId)) throw new InvalidOperationException("The new cluster identity is already in use.");
                        var first = selectedServers[0];
                        var defaults = await PresetCapture.ReadAsync(repository, new(first, name), token);
                        defaults = PresetLibrary.WithoutPasswords(defaults);
                        target = new(edit.TargetClusterId!.Value, name.Trim(), first.Edition, first.Settings.Copy() with { Name = name.Trim() })
                        { Defaults = defaults };
                    }
                    else target = document.Clusters.SingleOrDefault(c => c.Id == edit.TargetClusterId)
                        ?? throw new InvalidOperationException("The target cluster no longer exists.");
                    foreach (var affected in document.Servers.Where(s => s.ClusterId == target.Id || selectedServers.Any(selected => selected.Id == s.Id)))
                    {
                        if (statuses.SingleOrDefault(s => s.ServerId == affected.Id)?.State is not (ServerState.Stopped or ServerState.Failed))
                            throw new InvalidOperationException("Stop the affected servers and cluster before adding members.");
                        if ((await stopGuard.CheckAsync(affected.Directory, token)).State != InstallationStopState.Stopped)
                            throw new InvalidOperationException("An affected server could not be verified as stopped.");
                    }
                    var intent = await joining.PrepareAsync(request.Id, selectedServers, target, edit.NewClusterName is not null, token);
                    progress(new("Applying inherited settings and mods to the joining servers."));
                    await joining.ExecuteAsync(intent, token);
                    saved = await repository.ReadAsync(token); return;
                }
            }, ct);
            return saved!.Servers.Where(server => edit.Expected.Any(member => member.ServerId == server.Id)).Select(Review).ToArray();
        });
    }
}
