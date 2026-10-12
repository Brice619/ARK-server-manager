using System.Text.Json;
namespace ArkServerManager.Core;
public sealed record ClusterOverrideReview(Guid ServerId, Guid ClusterId, string Directory, List<ClusterSettingOverride> Overrides, bool MaxPlayers, bool Mods)
{ public string? SharedSettingsToken { get; init; } }
public sealed record ClusterOverrideSave(ClusterOverrideReview Expected, List<ClusterSettingOverride> Overrides, bool MaxPlayers, bool Mods, bool Confirmed)
{ public SaveChoice Choice { get; init; } = SaveChoice.SaveForNextStart; }
public sealed record ClusterOverrideSaveResult(ClusterOverrideReview Overrides, PresetApplicationSaveResult? Applied, string? ApplyError);
public sealed class ClusterOverrideManagerOperations(CatalogRepository repository, BackgroundOperations operations, ServerLifecycleCoordinator lifecycle,
    PresetApplicationReviewService? reviews = null, PresetApplicationManagerOperations? applications = null, PresetApplicationQueue? queued = null,
    Func<Guid, CancellationToken, Task>? requireOverrideAllowed = null)
{
    public static ClusterOverrideReview Review(ServerDefinition server) => new(server.Id, server.ClusterId ?? throw new ArgumentException("Choose a cluster map."), server.Directory,
        [.. server.ClusterOverrides], server.OverrideClusterMaxPlayers, server.OverrideClusterMods);
    public async Task<ClusterOverrideReview> ReadAsync(Guid id, CancellationToken ct)
    {
        var catalog = await repository.ReadAsync(ct);
        var review = Review(catalog.Servers.Single(server => server.Id == id));
        return review with { SharedSettingsToken = SharedToken(catalog.Clusters.Single(cluster => cluster.Id == review.ClusterId)) };
    }
    private static string SharedToken(ClusterDefinition cluster) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(cluster)));
    public BackgroundOperation Start(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<ClusterOverrideSave>() ?? throw new ArgumentException("Review map overrides.");
        if (!edit.Confirmed || edit.Expected is null || edit.Expected.ServerId == Guid.Empty || edit.Expected.ClusterId == Guid.Empty || !Enum.IsDefined(edit.Choice) || edit.Choice == SaveChoice.Cancel)
            throw new ArgumentException("Confirm map overrides.");
        ClusterSettingOverrides.Validate(edit.Overrides); ClusterSettingOverrides.Validate(edit.Expected.Overrides);
        return operations.Submit(request.Id, "cluster-overrides-save", request.Payload, async (progress, ct) =>
        {
            PresetApplicationReview? inheritance = null;
            void RequireReview(ServerDefinition server)
            {
                var current = Review(server);
                if (current.ClusterId != edit.Expected.ClusterId || current.Directory != edit.Expected.Directory || current.MaxPlayers != edit.Expected.MaxPlayers
                    || current.Mods != edit.Expected.Mods || !current.Overrides.SequenceEqual(edit.Expected.Overrides))
                    throw new InvalidOperationException("Map overrides or membership changed. Review them again.");
            }
            await lifecycle.WithStatusScopeAsync(async (_, token) =>
            {
                if (requireOverrideAllowed is not null) await requireOverrideAllowed(edit.Expected.ServerId, token);
                if (queued is not null && await queued.ReadAsync(edit.Expected.ServerId, token) is not null)
                    throw new InvalidOperationException("This map has saved preset or inheritance changes. Apply or review them before changing override choices.");
                var catalog = await repository.ReadAsync(token);
                var server = catalog.Servers.Single(s => s.Id == edit.Expected.ServerId);
                RequireReview(server);
                var released = edit.Expected.MaxPlayers && !edit.MaxPlayers || edit.Expected.Mods && !edit.Mods
                    || edit.Expected.Overrides.Any(key => !ClusterSettingOverrides.Contains(edit.Overrides, key.FileName, key.Section, key.Key));
                if (released && applications is not null && reviews is not null)
                {
                    var cluster = catalog.Clusters.Single(c => c.Id == server.ClusterId);
                    if (edit.Expected.SharedSettingsToken != SharedToken(cluster))
                        throw new InvalidOperationException("Shared cluster settings changed. Review map overrides again before restoring inheritance.");
                    var shared = cluster.Defaults ?? throw new InvalidOperationException("Define shared cluster settings before restoring inheritance.");
                    var reset = ClusterInheritance.BuildReset(server, edit, shared, "", "");
                    var prepared = await reviews.PrepareAsync(new(server.Id, request.Id)
                    {
                        DraftPreset = reset.Preset, Removals = reset.Removals,
                        OverridePolicy = new([.. edit.Overrides], edit.MaxPlayers, edit.Mods), SharedDefaultsToken = edit.Expected.SharedSettingsToken
                    }, token);
                    inheritance = prepared.Review;
                }
                else await repository.ChangeAsync(current =>
                {
                    RequireReview(current.Servers.Single(s => s.Id == server.Id));
                    current.SetClusterOverrides(server.Id, edit.Overrides, edit.MaxPlayers, edit.Mods);
                }, token);
            }, ct);
            if (inheritance is null) return new ClusterOverrideSaveResult(await ReadAsync(edit.Expected.ServerId, ct), null, null);
            try
            {
                var applied = await applications!.ApplyAsync(new(inheritance, edit.Choice, true), progress, ct);
                return new ClusterOverrideSaveResult(await ReadAsync(edit.Expected.ServerId, ct), applied, null);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            { return new ClusterOverrideSaveResult(await ReadAsync(edit.Expected.ServerId, ct), null,
                "Inherited settings and override choices could not finish applying. Review saved changes before retrying: " + error.Message); }
        });
    }
}
