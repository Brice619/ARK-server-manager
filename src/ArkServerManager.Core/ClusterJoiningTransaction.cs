using System.Text.Json;

namespace ArkServerManager.Core;

/// <summary>Caller owns the lifecycle/file scope throughout acceptance, publication and replay.</summary>
public sealed class ClusterJoiningTransaction(CatalogRepository repository, ClusterJoiningJournal journal,
    PresetApplicationReviewService reviews, PresetApplicationQueue applications,
    Func<ServerDefinition, CancellationToken, Task> requireStopped,
    Func<Guid, CancellationToken, Task> requireNoConflicts, Action<string>? checkpoint = null,
    Func<Guid, CancellationToken, Task>? requireTargetReady = null)
{
    public async Task<ClusterJoiningRecord> PrepareAsync(Guid operationId, IReadOnlyList<ServerDefinition> originals,
        ClusterDefinition target, bool createsCluster, CancellationToken ct)
    {
        if (requireTargetReady is not null) await requireTargetReady(target.Id, ct);
        var prepared = new List<ReviewedPresetApplication>();
        foreach (var server in originals)
        {
            await requireNoConflicts(server.Id, ct);
            if (await applications.ReadAsync(server.Id, ct) is not null)
                throw new InvalidOperationException("Apply or review this server's saved preset before adding it to a cluster.");
            await requireStopped(server, ct);
            prepared.Add(await reviews.PrepareJoiningAsync(server.Id, target, Guid.NewGuid(), ct));
        }
        return new(1, operationId, target, createsCluster, [.. originals], prepared);
    }

    public async Task ExecuteAsync(ClusterJoiningRecord record, CancellationToken ct)
    {
        await journal.AcceptAsync(record, ct);
        checkpoint?.Invoke("accepted");
        await ResumeAsync(record.OperationId, ct);
    }

    public async Task ResumeForServerAsync(Guid serverId, CancellationToken ct)
    {
        foreach (var record in (await journal.ReadAsync(ct)).Where(r => r.Originals.Any(s => s.Id == serverId)))
            await ResumeAsync(record.OperationId, ct);
    }

    public async Task<bool> ResumeAsync(Guid operationId, CancellationToken ct)
    {
        var record = (await journal.ReadAsync(ct)).SingleOrDefault(r => r.OperationId == operationId);
        if (record is null) return false;
        var current = await repository.ReadAsync(ct);
        RequireMembership(current, record);
        if (requireTargetReady is not null) await requireTargetReady(record.Target.Id, ct);
        foreach (var server in current.Servers.Where(s => s.ClusterId == record.Target.Id || record.Originals.Any(o => o.Id == s.Id)))
            await requireStopped(server, ct);
        foreach (var application in record.Applications)
        {
            await requireNoConflicts(application.Server.Id, ct);
            var pending = await applications.ReadAsync(application.Server.Id, ct);
            if (pending is not null)
            {
                if (!Same(pending.Application, application))
                    throw new InvalidOperationException("A different preset is pending for a joining server. The join remains recorded.");
            }
            else
            {
                var server = current.Servers.Single(s => s.Id == application.Server.Id);
                if (!Same(server, Desired(application)) || !await FilesMatchAsync(application, ct))
                    await applications.EnqueueAsync(application, ct);
            }
        }
        checkpoint?.Invoke("queued");
        await repository.ChangeAsync(catalog =>
        {
            var snapshot = new CatalogDocument(1, catalog.Clusters.ToList(), catalog.Servers.ToList());
            RequireMembership(snapshot, record);
            if (!catalog.Clusters.Any(c => c.Id == record.Target.Id)) catalog.AddCluster(record.Target);
            var unjoined = record.Originals.Where(o => catalog.Servers.Single(s => s.Id == o.Id).ClusterId is null).ToArray();
            if (unjoined.Length > 0)
            {
                catalog.SetClusterMembership(unjoined.Select(s => s.Id).ToArray(), record.Target.Id);
                foreach (var server in unjoined) catalog.SetClusterOverrides(server.Id, [], false, false);
            }
        }, ct);
        checkpoint?.Invoke("membership");
        foreach (var application in record.Applications)
        {
            await applications.ApplyBeforeStartAsync(application.Server.Id, ct);
            checkpoint?.Invoke("member-applied");
        }
        var finished = await repository.ReadAsync(ct);
        foreach (var application in record.Applications)
            if (!Same(finished.Servers.SingleOrDefault(s => s.Id == application.Server.Id), Desired(application))
                || !await FilesMatchAsync(application, ct))
                throw new InvalidOperationException("Cluster joining is not fully applied. Its recovery record was retained.");
        await journal.RemoveCompletedAsync(record, ct);
        checkpoint?.Invoke("completed");
        return true;
    }

    private async Task<bool> FilesMatchAsync(ReviewedPresetApplication application, CancellationToken ct)
    {
        var user = await ServerConfigurationReader.ReadAsync(repository, new(application.Server.Id, "GameUserSettings.ini"), ct);
        var game = await ServerConfigurationReader.ReadAsync(repository, new(application.Server.Id, "Game.ini"), ct);
        return user.Text == application.Plan.UserSettings && game.Text == application.Plan.GameSettings;
    }

    private static ServerDefinition Desired(ReviewedPresetApplication application) => application.Server with
    { Settings = application.Plan.Settings, Mods = application.Plan.Mods, ClusterOverrides = [], OverrideClusterMaxPlayers = false, OverrideClusterMods = false };

    private static bool Same<T>(T first, T second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);

    private static void RequireMembership(CatalogDocument current, ClusterJoiningRecord record)
    {
        var cluster = current.Clusters.SingleOrDefault(c => c.Id == record.Target.Id);
        if (cluster is null && !record.CreatesCluster || cluster is not null && !Same(cluster, record.Target))
            throw new InvalidOperationException("The target cluster changed. Its joining request was retained for review.");
        var originalCount = 0;
        foreach (var original in record.Originals)
        {
            var server = current.Servers.SingleOrDefault(s => s.Id == original.Id);
            if (Same(server, original)) { originalCount++; continue; }
            var application = record.Applications.Single(a => a.Server.Id == original.Id);
            if (!Same(server, application.Server) && !Same(server, Desired(application)))
                throw new InvalidOperationException("A joining server changed. Its accepted request was retained for review.");
        }
        if (originalCount != 0 && originalCount != record.Originals.Count)
            throw new InvalidOperationException("Joining membership is incomplete or changed. Review it before continuing.");
    }
}
