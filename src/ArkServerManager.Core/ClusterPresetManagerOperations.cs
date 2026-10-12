using System.Text.Json;
namespace ArkServerManager.Core;
public sealed record ClusterPresetSelection(Guid ClusterId, Guid PresetId, List<Guid> ServerIds)
{ public ServerPreset? DraftPreset { get; init; } }
public sealed record ClusterPresetReview(Guid ClusterId, Guid PresetId, List<PresetApplicationReview> Maps)
{ public string? ClusterDefaultsToken { get; init; } }
public sealed record ClusterPresetSave(ClusterPresetReview Expected, SaveChoice Choice, bool Confirmed)
{
    public bool UseForNewMaps { get; init; }
}
public sealed record ClusterPresetMapResult(Guid ServerId, PresetApplicationSaveResult? Result, string? Error);
public sealed record ClusterPresetSaveResult(Guid ClusterId, List<ClusterPresetMapResult> Maps)
{
    public bool DefaultsSaved { get; init; }
}
public sealed class ClusterPresetManagerOperations(CatalogRepository repository, PresetApplicationReviewService reviews,
    PresetApplicationManagerOperations applications, BackgroundOperations operations,
    Func<Func<CancellationToken, Task>, CancellationToken, Task>? withPublicationScope = null,
    Func<Guid, CancellationToken, Task>? requireDefaultsAllowed = null)
{
    public async Task<ClusterPresetReview> ReviewAsync(ClusterPresetSelection selection, CancellationToken ct = default)
    {
        ValidateSelection(selection.ClusterId, selection.PresetId, selection.ServerIds);
        var catalog = await repository.ReadAsync(ct);
        if (!catalog.Clusters.Any(cluster => cluster.Id == selection.ClusterId)) throw new ArgumentException("Choose a registered cluster.");
        var maps = new List<PresetApplicationReview>();
        foreach (var id in selection.ServerIds)
        {
            if (!catalog.Servers.Any(server => server.Id == id && server.ClusterId == selection.ClusterId))
                throw new InvalidOperationException("Selected cluster membership changed. Reload before reviewing.");
            maps.Add((await reviews.PrepareAsync(new(id, selection.PresetId) { PreserveClusterOverrides = true, DraftPreset = selection.DraftPreset }, ct)).Review);
        }
        return new(selection.ClusterId, selection.PresetId, maps)
        { ClusterDefaultsToken = DefaultsToken(catalog.Clusters.Single(cluster => cluster.Id == selection.ClusterId)) };
    }
    public BackgroundOperation Start(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<ClusterPresetSave>() ?? throw new ArgumentException("Review the cluster preset.");
        if (!edit.Confirmed || edit.Expected?.Maps is null || !Enum.IsDefined(edit.Choice) || edit.Choice == SaveChoice.Cancel)
            throw new ArgumentException("Confirm when to apply the cluster preset.");
        ValidateSelection(edit.Expected.ClusterId, edit.Expected.PresetId, edit.Expected.Maps.Select(map => map.ServerId).ToList());
        if (edit.Expected.Maps.Any(map => map.PresetId != edit.Expected.PresetId)) throw new ArgumentException("Review one preset for the selected maps.");
        var draft = JsonSerializer.Serialize(edit.Expected.Maps[0].DraftPreset);
        if (edit.Expected.Maps.Any(map => JsonSerializer.Serialize(map.DraftPreset) != draft)) throw new ArgumentException("Review the same shared settings for every selected map.");
        return operations.Submit(request.Id, "cluster-preset-save", request.Payload, async (progress, ct) =>
        {
            // Preflight every reviewed map before accepting any changes; each map retains its own durable application record.
            var catalog = await repository.ReadAsync(ct);
            RequireDefaultsUnchanged(catalog.Clusters.Single(cluster => cluster.Id == edit.Expected.ClusterId), edit.Expected.ClusterDefaultsToken);
            ReviewedPresetApplication? first = null;
            foreach (var map in edit.Expected.Maps)
            {
                if (!catalog.Servers.Any(server => server.Id == map.ServerId && server.ClusterId == edit.Expected.ClusterId))
                    throw new InvalidOperationException("Cluster membership changed. Review the preset again.");
                var prepared = await reviews.RebuildReviewedAsync(map, ct); first ??= prepared;
            }
            if (edit.UseForNewMaps)
            {
                var shared = first!.SharedPlan ?? first.Plan;
                async Task Publish(CancellationToken token)
                {
                if (requireDefaultsAllowed is not null) await requireDefaultsAllowed(edit.Expected.ClusterId, token);
                await repository.ChangeAsync(current =>
                {
                    RequireDefaultsUnchanged(current.Clusters.Single(cluster => cluster.Id == edit.Expected.ClusterId), edit.Expected.ClusterDefaultsToken);
                    current.SetClusterDefaultPreset(edit.Expected.ClusterId,
                        new ServerPreset(first.Review.PresetName, first.Server.Edition, shared.Settings.Copy(), [.. shared.Mods])
                        { UserSettings = shared.UserSettings, GameSettings = shared.GameSettings });
                }, token);
                }
                if (withPublicationScope is null) await Publish(ct); else await withPublicationScope(Publish, ct);
            }
            var results = new List<ClusterPresetMapResult>();
            foreach (var map in edit.Expected.Maps)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = await applications.ApplyAsync(new(map, edit.Choice, true),
                        message => progress(new(map.ServerName + ": " + message.Message)), ct);
                    results.Add(new(map.ServerId, result, null));
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
                { results.Add(new(map.ServerId, null, error.Message)); }
            }
            return new ClusterPresetSaveResult(edit.Expected.ClusterId, results) { DefaultsSaved = edit.UseForNewMaps };
        });
    }
    private static void ValidateSelection(Guid cluster, Guid preset, List<Guid> ids)
    {
        if (cluster == Guid.Empty || preset == Guid.Empty || ids is null || ids.Count is < 1 or > 128
            || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Choose a cluster, preset and distinct map selection.");
    }
    private static string DefaultsToken(ClusterDefinition cluster) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(cluster)));
    private static void RequireDefaultsUnchanged(ClusterDefinition cluster, string? token)
    {
        if (token is null || token.Length != 64 || DefaultsToken(cluster) != token)
            throw new InvalidOperationException("Cluster defaults changed or were not reviewed. Review shared settings again.");
    }
}
