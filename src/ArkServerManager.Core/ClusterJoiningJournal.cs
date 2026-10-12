using System.Collections.Concurrent;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ClusterJoiningStatus(Guid OperationId, Guid ClusterId, string ClusterName, List<Guid> ServerIds);
public sealed record ClusterJoiningRecoveryResult(Guid OperationId, bool Completed);

public sealed record ClusterJoiningRecord(int SchemaVersion, Guid OperationId, ClusterDefinition Target,
    bool CreatesCluster, List<ServerDefinition> Originals, List<ReviewedPresetApplication> Applications)
{
    public override string ToString() => $"Cluster joining {OperationId}; configuration values redacted";
}

/// <summary>Private acceptance checkpoint for a batch join. Acceptance changes no membership or server files.</summary>
public sealed class ClusterJoiningJournal(string managementRoot)
{
    private readonly string folder = Path.Combine(Path.GetFullPath(managementRoot), "app-settings", "cluster-joining");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private SemaphoreSlim Gate => Gates.GetOrAdd(folder, _ => new(1, 1));
    private string PathFor(Guid id) => Path.Combine(folder, id.ToString("N") + ".json");

    public async Task AcceptAsync(ClusterJoiningRecord record, CancellationToken ct = default)
    {
        // Snapshot mutable collections once, then validate and persist that same snapshot.
        var accepted = JsonSerializer.Deserialize<ClusterJoiningRecord>(JsonSerializer.SerializeToUtf8Bytes(record))
            ?? throw new InvalidDataException("Review cluster joining.");
        Validate(accepted);
        await Gate.WaitAsync(ct);
        try
        {
            var pending = await ReadCoreAsync(ct);
            var existing = pending.SingleOrDefault(r => r.OperationId == accepted.OperationId);
            if (existing is not null)
            {
                if (JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(accepted))
                    throw new InvalidOperationException("The accepted cluster-joining request changed.");
                return;
            }
            var ids = accepted.Originals.Select(s => s.Id).ToHashSet();
            if (pending.Any(r => r.Originals.Any(s => ids.Contains(s.Id))))
                throw new InvalidOperationException("A selected server already has an accepted cluster-joining request.");
            await new AtomicJsonStore<ClusterJoiningRecord>(PathFor(accepted.OperationId), true).SaveAsync(accepted, ct);
        }
        finally { Gate.Release(); }
    }

    public async Task<IReadOnlyList<ClusterJoiningRecord>> ReadAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try { return await ReadCoreAsync(ct); }
        finally { Gate.Release(); }
    }

    /// <summary>Caller has verified every member's final catalog and configuration under the lifecycle scope.</summary>
    public async Task RemoveCompletedAsync(ClusterJoiningRecord expected, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var current = (await ReadCoreAsync(ct)).SingleOrDefault(r => r.OperationId == expected.OperationId);
            if (current is null) return;
            if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(expected))
                throw new InvalidOperationException("The accepted cluster-joining request changed before cleanup.");
            File.Delete(PathFor(expected.OperationId));
        }
        finally { Gate.Release(); }
    }

    public async Task RemoveDeletedAsync(CatalogDocument catalog, ServerDeletionRecord deletion, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            DeletedServerRecordCleanup.RequireCompleted(catalog, deletion);
            foreach (var record in (await ReadCoreAsync(ct)).Where(r => r.Originals.Any(s => s.Id == deletion.ServerId)))
            {
                DeletedServerRecordCleanup.RequireDirectory(deletion, record.Originals.Single(s => s.Id == deletion.ServerId).Directory);
                var remaining = record with
                {
                    Originals = record.Originals.Where(s => s.Id != deletion.ServerId).ToList(),
                    Applications = record.Applications.Where(a => a.Server.Id != deletion.ServerId).ToList()
                };
                if (remaining.Originals.Count == 0) File.Delete(PathFor(record.OperationId));
                else
                {
                    Validate(remaining);
                    await new AtomicJsonStore<ClusterJoiningRecord>(PathFor(record.OperationId), true).SaveAsync(remaining, ct);
                }
            }
        }
        finally { Gate.Release(); }
    }

    private async Task<List<ClusterJoiningRecord>> ReadCoreAsync(CancellationToken ct)
    {
        CheckPath(folder);
        if (!Directory.Exists(folder)) return [];
        var files = Directory.GetFiles(folder, "*.json");
        if (files.Length > 1024) throw new InvalidDataException("Too many pending cluster-joining records.");
        var result = new List<ClusterJoiningRecord>();
        foreach (var path in files.Order(StringComparer.Ordinal))
        {
            CheckPath(path);
            var record = await new AtomicJsonStore<ClusterJoiningRecord>(path, true).LoadAsync(ct)
                ?? throw new InvalidDataException("A cluster-joining record disappeared.");
            Validate(record);
            if (Path.GetFileName(path) != record.OperationId.ToString("N") + ".json")
                throw new InvalidDataException("The cluster-joining record identity does not match its file.");
            result.Add(record);
        }
        return result;
    }
    private static void CheckPath(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cluster-joining records must not follow linked paths.");
    }

    private static void Validate(ClusterJoiningRecord record)
    {
        if (record.SchemaVersion != 1 || record.OperationId == Guid.Empty || record.Target is null
            || record.Originals is null || record.Applications is null || record.Originals.Count is < 1 or > 1024
            || record.Originals.Count != record.Applications.Count || record.Originals.Any(s => s is null || s.Id == Guid.Empty || s.ClusterId is not null)
            || record.Originals.Select(s => s.Id).Distinct().Count() != record.Originals.Count
            || record.Applications.Any(a => a is null || a.Server is null || a.Plan is null || a.Review is null)
            || record.Applications.Select(a => a.Server.Id).Distinct().Count() != record.Applications.Count)
            throw new InvalidDataException("Invalid cluster-joining acceptance record.");
        var validator = new ServerCatalog(); validator.AddCluster(record.Target);
        foreach (var original in record.Originals)
        {
            validator.AddServer(original);
            var application = record.Applications.SingleOrDefault(a => a.Server.Id == original.Id)
                ?? throw new InvalidDataException("A cluster-joining member intent is missing.");
            var proposed = original with { ClusterId = record.Target.Id, ClusterOverrides = [], OverrideClusterMaxPlayers = false, OverrideClusterMods = false };
            if (original.Edition != record.Target.Edition || !Path.IsPathFullyQualified(original.Directory)
                || JsonSerializer.Serialize(proposed) != JsonSerializer.Serialize(application.Server)
                || application.Plan.ServerId != original.Id || application.Review.ServerId != original.Id
                || application.Plan.OverridePolicy is not { Overrides.Count: 0, MaxPlayers: false, Mods: false }
                || application.Review.OverridePolicy is not { Overrides.Count: 0, MaxPlayers: false, Mods: false }
                || !Hash(application.UserSourceSHA256) || application.GameSourceSHA256 is not null && !Hash(application.GameSourceSHA256)
                || !Hash(application.Review.ReviewToken) || application.Plan.UserSettings is null || application.Plan.GameSettings is null
                || ConfigurationSyntax.Check(application.Plan.UserSettings).Count > 0 || ConfigurationSyntax.Check(application.Plan.GameSettings).Count > 0)
                throw new InvalidDataException("Cluster-joining configuration belongs to another member or policy.");
            application.Plan.Settings.Validate();
            AsaModLaunchArguments.Build(application.Plan.Mods);
            if (application.Plan.Settings.Name != original.Settings.Name || application.Plan.Settings.GamePort != original.Settings.GamePort
                || application.Plan.Settings.QueryPort != original.Settings.QueryPort || application.Plan.Settings.RconPort != original.Settings.RconPort)
                throw new InvalidDataException("Cluster joining must preserve member identity and ports.");
        }
    }
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
