using System.Security.Cryptography;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record PresetApplicationSelection(Guid ServerId, Guid PresetId)
{ public bool PreserveClusterOverrides { get; init; } public ServerPreset? DraftPreset { get; init; } public List<ClusterSettingOverride> Removals { get; init; } = [];
    public ClusterOverridePolicy? OverridePolicy { get; init; } public string? SharedDefaultsToken { get; init; } }
public sealed record PresetApplicationReview(Guid ServerId, Guid PresetId, string ServerName, string PresetName,
    ArkEdition Edition, int CurrentMaxPlayers, int DesiredMaxPlayers, List<string> AddedMods, List<string> RemovedMods,
    int ConfigurationChanges, string ReviewToken)
{
    public bool PreserveClusterOverrides { get; init; }
    public ServerPreset? DraftPreset { get; init; }
    public List<ClusterSettingOverride> Removals { get; init; } = [];
    public ClusterOverridePolicy? OverridePolicy { get; init; }
    public string? SharedDefaultsToken { get; init; }
    public override string ToString() => $"Preset review for {ServerId}; configuration values redacted";
}
public sealed record ReviewedPresetApplication(PresetApplicationReview Review, ServerDefinition Server,
    PresetApplicationPlan Plan, string UserSourceSHA256, string? GameSourceSHA256)
{
    public PresetApplicationPlan? SharedPlan { get; init; }
    public override string ToString() => $"Reviewed preset for {Server.Id}; configuration values redacted";
}

public sealed class PresetApplicationReviewService(CatalogRepository repository, PresetLibrary library,
    Func<Guid, string, CancellationToken, Task<ServerConfigurationFile>> readConfiguration)
{
    public PresetApplicationReviewService(CatalogRepository repository, PresetLibrary library)
        : this(repository, library, (id, file, ct) => ServerConfigurationReader.ReadAsync(repository, new(id, file), ct)) { }

    public async Task<ReviewedPresetApplication> PrepareAsync(PresetApplicationSelection selection, CancellationToken ct = default)
    {
        if (selection.ServerId == Guid.Empty || selection.PresetId == Guid.Empty) throw new ArgumentException("Choose a server and saved preset.");
        var catalog = await repository.ReadAsync(ct);
        return await PrepareCatalogAsync(selection, catalog, ct);
    }

    /// <summary>Prepares inherited configuration against the proposed membership without publishing it.
    /// The joining transaction must persist this intent before changing membership or files.</summary>
    public async Task<ReviewedPresetApplication> PrepareJoiningAsync(Guid serverId, ClusterDefinition target, Guid reviewId, CancellationToken ct = default)
    {
        if (serverId == Guid.Empty || reviewId == Guid.Empty) throw new ArgumentException("Choose a server and joining review identity.");
        var current = await repository.ReadAsync(ct);
        var original = current.Servers.SingleOrDefault(s => s.Id == serverId)
            ?? throw new ArgumentException("The selected server no longer exists.");
        if (original.ClusterId is not null || original.Edition != target.Edition)
            throw new ArgumentException("Choose a standalone server for the same ARK edition.");
        var existing = current.Clusters.SingleOrDefault(c => c.Id == target.Id);
        if (existing is not null && JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(target))
            throw new InvalidOperationException("Target cluster defaults changed. Review joining again.");
        var validation = new ServerCatalog(); validation.AddCluster(target);
        var proposed = original with { ClusterId = target.Id, ClusterOverrides = [], OverrideClusterMaxPlayers = false, OverrideClusterMods = false };
        var projected = current with
        {
            Clusters = existing is null ? [.. current.Clusters, target] : current.Clusters,
            Servers = current.Servers.Select(s => s.Id == serverId ? proposed : s).ToList()
        };
        var shared = target.Defaults ?? new ServerPreset(target.Name, target.Edition, target.Settings.Copy(), []);
        return await PrepareCatalogAsync(new(serverId, reviewId)
        {
            DraftPreset = shared,
            OverridePolicy = new([], false, false),
            SharedDefaultsToken = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(target)))
        }, projected, ct);
    }

    private async Task<ReviewedPresetApplication> PrepareCatalogAsync(PresetApplicationSelection selection, CatalogDocument catalog, CancellationToken ct)
    {
        var server = catalog.Servers.SingleOrDefault(s => s.Id == selection.ServerId)
            ?? throw new ArgumentException("The selected server no longer exists.");
        ClusterOverridePolicy? policy = null;
        if (selection.OverridePolicy is { } selectedPolicy)
        {
            ClusterSettingOverrides.Validate(selectedPolicy.Overrides);
            if (server.ClusterId is not Guid clusterId || selection.SharedDefaultsToken != Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(catalog.Clusters.Single(c => c.Id == clusterId)))))
                throw new InvalidOperationException("Shared cluster settings changed. Review inheritance again.");
            policy = selectedPolicy with { Overrides = [.. selectedPolicy.Overrides] };
        }
        var preset = selection.DraftPreset is null ? (await library.ListAsync(ct)).SingleOrDefault(p => p.Id == selection.PresetId)
            ?? throw new ArgumentException("The selected preset no longer exists.")
            : new PresetEntry(selection.PresetId, RedactDraft(PresetLibrary.ReadImport(JsonSerializer.SerializeToUtf8Bytes(new PresetExportDocument(1, selection.DraftPreset)))));
        var user = await readConfiguration(server.Id, "GameUserSettings.ini", ct);
        var game = await readConfiguration(server.Id, "Game.ini", ct);
        if (user.ServerId != server.Id || game.ServerId != server.Id || user.FileName != "GameUserSettings.ini" || game.FileName != "Game.ini"
            || user.Platform != game.Platform || user.Text is null || user.SourceSHA256 is null
            || game.Text is not null && game.SourceSHA256 is null)
            throw new InvalidDataException("The current configuration could not be verified for preset review.");
        var plan = PresetApplication.Build(server, preset.Preset, user.Text, game.Text ?? "", selection.PreserveClusterOverrides, selection.Removals)
            with { OverridePolicy = policy };
        var token = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { Server = server, Preset = preset, UserHash = user.SourceSHA256, GameHash = game.SourceSHA256, selection.PreserveClusterOverrides, selection.Removals,
                Policy = policy, selection.SharedDefaultsToken })));
        var review = new PresetApplicationReview(server.Id, preset.Id, server.Settings.Name, preset.Preset.Name, server.Edition,
            server.Settings.MaxPlayers, plan.Settings.MaxPlayers, plan.Mods.Except(server.Mods).ToList(), server.Mods.Except(plan.Mods).ToList(),
            CountChanges(user.Text, plan.UserSettings) + CountChanges(game.Text ?? "", plan.GameSettings), token)
            { PreserveClusterOverrides = selection.PreserveClusterOverrides, DraftPreset = selection.DraftPreset is null ? null : preset.Preset, Removals = [.. selection.Removals],
                OverridePolicy = policy is null ? null : policy with { Overrides = [.. policy.Overrides] }, SharedDefaultsToken = selection.SharedDefaultsToken };
        return new(review, server, plan, user.SourceSHA256, game.SourceSHA256)
        { SharedPlan = selection.PreserveClusterOverrides ? PresetApplication.Build(server, preset.Preset, user.Text, game.Text ?? "") : plan };
    }

    public async Task<ReviewedPresetApplication> RebuildReviewedAsync(PresetApplicationReview expected, CancellationToken ct = default)
    {
        if (expected.ReviewToken is null || expected.ReviewToken.Length != 64 || expected.ReviewToken.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Review the preset changes before applying them.");
        var current = await PrepareAsync(new(expected.ServerId, expected.PresetId) { PreserveClusterOverrides = expected.PreserveClusterOverrides, DraftPreset = expected.DraftPreset, Removals = expected.Removals,
            OverridePolicy = expected.OverridePolicy, SharedDefaultsToken = expected.SharedDefaultsToken }, ct);
        if (!string.Equals(current.Review.ReviewToken, expected.ReviewToken, StringComparison.Ordinal))
            throw new InvalidOperationException("The server, configuration or preset changed. Review the changes again.");
        return current;
    }

    private static int CountChanges(string current, string desired)
    {
        var before = new IniDocument(current);
        var after = new IniDocument(desired);
        return before.Entries().Concat(after.Entries())
            .GroupBy(e => e.Section + "\0" + e.Key, StringComparer.OrdinalIgnoreCase)
            .Count(group => !before.GetValues(group.First().Section, group.First().Key)
                .SequenceEqual(after.GetValues(group.First().Section, group.First().Key)));
    }
    private static ServerPreset RedactDraft(ServerPreset preset)
    {
        static string? Strip(string? text)
        {
            if (text is null) return null;
            var ini = new IniDocument(text); ini.RemoveKeys(["ServerPassword", "ServerAdminPassword", "AdminPassword"]); return ini.ToString();
        }
        var settings = preset.Settings.Copy();
        foreach (var key in settings.Values.Keys.Where(PresetLibrary.IsPasswordSetting).ToArray()) settings.Values.Remove(key);
        return preset with { Settings = settings, Mods = [.. preset.Mods], UserSettings = Strip(preset.UserSettings), GameSettings = Strip(preset.GameSettings) };
    }
}
