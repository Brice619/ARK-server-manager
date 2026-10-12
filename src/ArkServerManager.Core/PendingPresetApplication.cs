using System.Collections.Concurrent;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record PendingPresetApplication(ReviewedPresetApplication Application, bool Started = false)
{
    public override string ToString() => $"Pending preset for {Application.Review.ServerId}; configuration values redacted";
}
public sealed record PendingPresetApplications(int SchemaVersion, List<PendingPresetApplication> Changes);
public sealed record PresetApplicationPendingStatus(PresetApplicationReview Review, bool Started);
public sealed record PresetApplicationPendingResult(Guid ServerId, PresetApplicationPendingStatus? Pending);
public sealed record PresetApplicationDiscard(Guid ServerId, string ReviewToken, bool Confirmed);
public sealed record PresetApplicationDiscardResult(Guid ServerId, bool Discarded);

/// <summary>The caller owns the selected server's stopped lifecycle/file scope during application.
/// A durable record survives partial configuration writes, catalog publication and mod cleanup.</summary>
public sealed class PresetApplicationQueue(CatalogRepository repository, string pendingFile,
    Func<ServerDefinition, CancellationToken, Task> requireStopped,
    Func<ServerDefinition, IReadOnlyList<string>, CancellationToken, Task> removeModFiles,
    bool supportsAsaRemoval = false)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = Gates.GetOrAdd(Path.GetFullPath(pendingFile), _ => new(1, 1));
    private readonly AtomicJsonStore<PendingPresetApplications> store = new(pendingFile, privateFile: true);

    /// <summary>Called inside the lifecycle scope before accepting an immediate save.</summary>
    public Task VerifyStoppedAsync(ServerDefinition server, CancellationToken ct = default) => requireStopped(server, ct);

    public async Task RemoveDeletedAsync(ServerDeletionRecord deletion, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            DeletedServerRecordCleanup.RequireCompleted(await repository.ReadAsync(ct), deletion);
            var document = await LoadAsync(ct);
            var entry = document.Changes.SingleOrDefault(p => p.Application.Review.ServerId == deletion.ServerId);
            if (entry is null) return;
            DeletedServerRecordCleanup.RequireDirectory(deletion, entry.Application.Server.Directory);
            await store.SaveAsync(document with { Changes = document.Changes.Where(p => p.Application.Review.ServerId != deletion.ServerId).ToList() }, ct);
        }
        finally { gate.Release(); }
    }

    public async Task EnqueueAsync(ReviewedPresetApplication application, CancellationToken ct = default)
    {
        Validate(new(application));
        await gate.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);
            var existing = document.Changes.SingleOrDefault(p => p.Application.Review.ServerId == application.Review.ServerId);
            if (existing is not null)
            {
                if (!existing.Started && Same(existing.Application, application)) return;
                throw new InvalidOperationException("A saved preset still needs to be applied or reviewed before saving another.");
            }
            await store.SaveAsync(document with { Changes = [.. document.Changes, new(application)] }, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<PendingPresetApplication?> ReadAsync(Guid serverId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).Changes.SingleOrDefault(p => p.Application.Review.ServerId == serverId); }
        finally { gate.Release(); }
    }

    public async Task<PresetApplicationPendingResult> ReadStatusAsync(Guid serverId, CancellationToken ct = default)
    {
        var pending = await ReadAsync(serverId, ct);
        return new(serverId, pending is null ? null : new(pending.Application.Review, pending.Started));
    }

    /// <summary>Only an untouched queue entry can be canceled; partial effects require recovery.</summary>
    public async Task DiscardUnstartedAsync(PresetApplicationDiscard request, CancellationToken ct = default)
    {
        if (!request.Confirmed || request.ServerId == Guid.Empty || !Hash(request.ReviewToken))
            throw new ArgumentException("Review and confirm the saved preset before canceling it.");
        await gate.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);
            var pending = document.Changes.SingleOrDefault(p => p.Application.Review.ServerId == request.ServerId)
                ?? throw new InvalidOperationException("The saved preset changed or was already applied. Refresh its status.");
            if (pending.Started) throw new InvalidOperationException("Preset application has already begun. Its recovery record cannot be discarded.");
            if (pending.Application.Review.ReviewToken != request.ReviewToken)
                throw new InvalidOperationException("The saved preset changed. Review it again before canceling.");
            await store.SaveAsync(document with { Changes = document.Changes.Where(p => p.Application.Review.ServerId != request.ServerId).ToList() }, ct);
        }
        finally { gate.Release(); }
    }

    public async Task ApplyBeforeStartAsync(Guid serverId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);
            var pending = document.Changes.SingleOrDefault(p => p.Application.Review.ServerId == serverId);
            if (pending is null) return;
            var application = pending.Application;
            var desired = application.Server with { Settings = application.Plan.Settings, Mods = application.Plan.Mods };
            if (application.Plan.OverridePolicy is { } desiredPolicy)
                desired = desired with { ClusterOverrides = [.. desiredPolicy.Overrides], OverrideClusterMaxPlayers = desiredPolicy.MaxPlayers, OverrideClusterMods = desiredPolicy.Mods };
            var current = (await repository.ReadAsync(ct)).Servers.SingleOrDefault(s => s.Id == serverId)
                ?? throw new InvalidOperationException("The preset's selected server no longer exists.");
            RequireServer(current);
            ServerCatalog.RequireModClientCompatibility(current, application.Plan.Mods.Count);
            await requireStopped(current, ct);
            // Check both files before touching either one. On retry, only this recorded
            // transaction's exact desired text or its original source is acceptable.
            var user = await ReadVerifiedAsync("GameUserSettings.ini", application.UserSourceSHA256, application.Plan.UserSettings, ct);
            var game = await ReadVerifiedAsync("Game.ini", application.GameSourceSHA256, application.Plan.GameSettings, ct);
            if (!pending.Started)
            {
                pending = pending with { Started = true };
                document = document with { Changes = document.Changes.Select(p => p.Application.Review.ServerId == serverId ? pending : p).ToList() };
                await store.SaveAsync(document, ct);
            }
            await WriteAsync(game, application.Plan.GameSettings, ct);
            await WriteAsync(user, application.Plan.UserSettings, ct);
            await repository.ChangeAsync(catalog =>
            {
                var latest = catalog.Servers.Single(s => s.Id == serverId);
                RequireServer(latest);
                catalog.UpdateSettings(serverId, application.Plan.Settings);
                catalog.ReplaceMods(serverId, application.Plan.Mods);
                if (application.Plan.OverridePolicy is { } policy)
                    catalog.SetClusterOverrides(serverId, policy.Overrides, policy.MaxPlayers, policy.Mods);
            }, ct);
            var wanted = application.Plan.Mods.Select(ulong.Parse).ToHashSet();
            var removed = application.Server.Mods.Where(id => !wanted.Contains(ulong.Parse(id))).ToArray();
            if (removed.Length > 0)
            {
                await requireStopped(desired, ct);
                await removeModFiles(application.Server, removed, ct);
            }
            await store.SaveAsync(document with { Changes = document.Changes.Where(p => p.Application.Review.ServerId != serverId).ToList() }, ct);

            void RequireServer(ServerDefinition selected)
            {
                if (!Same(selected, application.Server) && !(pending.Started && Same(selected, desired)))
                    throw new InvalidOperationException("The server changed after the preset was saved. Its pending change was retained for review.");
            }
            async Task<ServerConfigurationFile> ReadVerifiedAsync(string file, string? sourceHash, string text, CancellationToken token)
            {
                var source = await ServerConfigurationReader.ReadAsync(repository, new(serverId, file), token);
                if (!string.Equals(source.SourceSHA256, sourceHash, StringComparison.OrdinalIgnoreCase)
                    && source.Text != text)
                    throw new InvalidOperationException("The configuration changed after the preset was saved. Its pending change was retained for review.");
                return source;
            }
            async Task WriteAsync(ServerConfigurationFile source, string text, CancellationToken token)
            {
                if (source.Text == text) return;
                async Task Stopped(ServerDefinition selected, Func<CancellationToken, Task> change, CancellationToken cancellation)
                {
                    RequireServer(selected);
                    await requireStopped(selected, cancellation);
                    await change(cancellation);
                }
                if (source.Text is null)
                    await ServerConfigurationWriter.CreateGameAsync(repository, serverId, text, Stopped, token);
                else
                    await ServerConfigurationWriter.WriteAsync(repository, new(serverId, source.FileName, source.SourceSHA256!, text), Stopped, token);
            }
        }
        finally { gate.Release(); }
    }

    private async Task<PendingPresetApplications> LoadAsync(CancellationToken ct)
    {
        var document = await store.LoadAsync(ct) ?? new(1, []);
        if (document.SchemaVersion != 1 || document.Changes is null)
            throw new InvalidDataException("Invalid pending preset document.");
        foreach (var pending in document.Changes) Validate(pending);
        if (document.Changes.Select(p => p.Application.Review.ServerId).Distinct().Count() != document.Changes.Count)
            throw new InvalidDataException("Duplicate pending preset changes.");
        return document;
    }

    private void Validate(PendingPresetApplication pending)
    {
        if (pending?.Application is not { } app || app.Review is null || app.Server is null || app.Plan is null
            || app.Review.ServerId == Guid.Empty || app.Review.PresetId == Guid.Empty
            || app.Plan.ServerId != app.Server.Id || app.Review.ServerId != app.Server.Id
            || app.Review.Edition != app.Server.Edition || !Path.IsPathFullyQualified(app.Server.Directory)
            || app.Server.Settings is null || app.Plan.Settings is null || app.Server.Mods is null || app.Plan.Mods is null
            || app.Plan.UserSettings is null || app.Plan.GameSettings is null || !Hash(app.UserSourceSHA256)
            || app.GameSourceSHA256 is not null && !Hash(app.GameSourceSHA256) || !Hash(app.Review.ReviewToken))
            throw new InvalidDataException("Invalid saved preset application.");
        app.Server.Settings.Validate(); app.Plan.Settings.Validate();
        if (!Same(app.Plan.OverridePolicy, app.Review.OverridePolicy)) throw new InvalidDataException("Saved inheritance policy does not match its review.");
        if (app.Plan.OverridePolicy is { } policy)
        {
            ClusterSettingOverrides.Validate(policy.Overrides);
            if (app.Server.ClusterId is null)
                throw new InvalidDataException("Invalid saved inheritance policy.");
        }
        AsaModLaunchArguments.Build(app.Server.Mods); AsaModLaunchArguments.Build(app.Plan.Mods);
        ServerCatalog.RequireModClientCompatibility(app.Server, app.Plan.Mods.Count);
        if (app.Plan.Settings.Name != app.Server.Settings.Name || app.Plan.Settings.GamePort != app.Server.Settings.GamePort
            || app.Plan.Settings.QueryPort != app.Server.Settings.QueryPort || app.Plan.Settings.RconPort != app.Server.Settings.RconPort)
            throw new InvalidDataException("A saved preset cannot change server identity or ports.");
        if (!supportsAsaRemoval && app.Server.Edition == ArkEdition.ASA && app.Server.Mods.Select(ulong.Parse).Except(app.Plan.Mods.Select(ulong.Parse)).Any())
            throw new NotSupportedException("ASA mod removal needs its verified native cache cleanup adapter.");
        foreach (var text in new[] { app.Plan.UserSettings, app.Plan.GameSettings })
            if (System.Text.Encoding.UTF8.GetByteCount(text) > 4 * 1024 * 1024 || text.Contains('\0') || ConfigurationSyntax.Check(text).Count > 0)
                throw new InvalidDataException("Invalid saved preset configuration.");
    }
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);
    private static bool Same<T>(T a, T b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
}
