using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ServerAccessReview(Guid ServerId, string Directory, ArkEdition Edition, string Map, Guid? ClusterId,
    string SourceSHA256, bool AdministratorConfigured, bool JoinPasswordConfigured,
    Guid? PendingId, bool Applying);
public sealed record ServerAccessChange(ServerAccessReview Expected, string? AdministratorPassword, string? JoinPassword)
{
    public override string ToString() => $"Server access change for {Expected?.ServerId}; passwords redacted";
}
public sealed record PendingServerAccess(Guid Id, Guid ServerId, string Directory, ArkEdition Edition, string Map, Guid? ClusterId,
    string? AdministratorPassword, string? JoinPassword, string AdministratorSourceHash, string JoinSourceHash, bool Started = false)
{
    public override string ToString() => $"Pending access for {ServerId}; passwords redacted";
}
public sealed record PendingServerAccessDocument(int SchemaVersion, List<PendingServerAccess> Changes);

/// <summary>Credential patches preserve unrelated configuration and apply at every verified stopped startup.</summary>
public sealed class ServerAccessChanges(CatalogRepository repository, string pendingFile,
    Func<ServerDefinition, CancellationToken, Task> requireStopped)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = Gates.GetOrAdd(Path.GetFullPath(pendingFile), _ => new(1, 1));
    private readonly AtomicJsonStore<PendingServerAccessDocument> store = new(pendingFile, privateFile: true);

    public async Task RemoveDeletedAsync(ServerDeletionRecord deletion, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            DeletedServerRecordCleanup.RequireCompleted(await repository.ReadAsync(ct), deletion);
            var document = await LoadAsync(ct);
            var entry = document.Changes.SingleOrDefault(p => p.ServerId == deletion.ServerId);
            if (entry is null) return;
            DeletedServerRecordCleanup.RequireDirectory(deletion, entry.Directory);
            await store.SaveAsync(document with { Changes = document.Changes.Where(p => p.ServerId != deletion.ServerId).ToList() }, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<ServerAccessReview> ReadAsync(Guid id, CancellationToken ct = default)
    {
        var source = await ServerConfigurationReader.ReadAsync(repository, new(id, "GameUserSettings.ini"), ct);
        var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == id);
        var values = Credentials(source);
        await gate.WaitAsync(ct);
        try
        {
            var pending = (await LoadAsync(ct)).Changes.SingleOrDefault(p => p.ServerId == id);
            return new(id, server.Directory, server.Edition, server.Map, server.ClusterId, source.SourceSHA256!,
                values.Admin.Length > 0, values.Join.Length > 0, pending?.Id, pending?.Started ?? false);
        }
        finally { gate.Release(); }
    }

    /// <summary>The caller serializes this with configuration edits and lifecycle operations.</summary>
    public async Task QueueAsync(ServerAccessChange change, CancellationToken ct = default)
    {
        if (change.Expected is null || change.Expected.ServerId == Guid.Empty || !Hash(change.Expected.SourceSHA256)
            || change.AdministratorPassword is null && change.JoinPassword is null)
            throw new ArgumentException("Review and enter at least one password change.");
        await gate.WaitAsync(ct);
        try
        {
            var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == change.Expected.ServerId);
            if (server.Directory != change.Expected.Directory || server.Edition != change.Expected.Edition
                || server.Map != change.Expected.Map || server.ClusterId != change.Expected.ClusterId)
                throw new InvalidOperationException("The reviewed server installation changed. Review access settings again.");
            var source = await ServerConfigurationReader.ReadAsync(repository, new(server.Id, "GameUserSettings.ini"), ct);
            if (!string.Equals(source.SourceSHA256, change.Expected.SourceSHA256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The configuration changed. Review access settings again.");
            var values = Credentials(source);
            ServerAccessConfiguration.ValidatePasswords(change.AdministratorPassword ?? values.Admin, change.JoinPassword ?? values.Join);
            var document = await LoadAsync(ct);
            if (document.Changes.Any(p => p.ServerId == change.Expected.ServerId))
                throw new InvalidOperationException("A password change is already saved for the next start. Apply or cancel it before saving another.");
            var pending = new PendingServerAccess(Guid.NewGuid(), server.Id, server.Directory, server.Edition, server.Map, server.ClusterId,
                change.AdministratorPassword, change.JoinPassword, Fingerprint(values.Admin), Fingerprint(values.Join));
            await store.SaveAsync(document with { Changes = [.. document.Changes, pending] }, ct);
        }
        finally { gate.Release(); }
    }

    public async Task ApplyBeforeStartAsync(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);
            var pending = document.Changes.SingleOrDefault(p => p.ServerId == id);
            if (pending is null) return;
            var server = (await repository.ReadAsync(ct)).Servers.SingleOrDefault(s => s.Id == id)
                ?? throw new InvalidOperationException("The saved password's server no longer exists.");
            RequireSame(server);
            await requireStopped(server, ct);
            var source = await ServerConfigurationReader.ReadAsync(repository, new(id, "GameUserSettings.ini"), ct);
            var values = Credentials(source);
            RequireTarget(values.Admin, pending.AdministratorPassword, pending.AdministratorSourceHash);
            RequireTarget(values.Join, pending.JoinPassword, pending.JoinSourceHash);
            var text = ServerAccessConfiguration.Apply(source.Text!, server.Settings.RconPort,
                pending.AdministratorPassword ?? values.Admin, pending.JoinPassword ?? values.Join);
            if (!pending.Started)
            {
                pending = pending with { Started = true };
                document = document with { Changes = document.Changes.Select(p => p.Id == pending.Id ? pending : p).ToList() };
                await store.SaveAsync(document, ct);
            }
            await ServerConfigurationWriter.WriteAsync(repository, new(id, "GameUserSettings.ini", source.SourceSHA256!, text),
                async (selected, write, token) => { RequireSame(selected); await requireStopped(selected, token); await write(token); }, ct);
            await store.SaveAsync(document with { Changes = document.Changes.Where(p => p.ServerId != id).ToList() }, ct);

            void RequireSame(ServerDefinition selected)
            {
                if (selected.Directory != pending.Directory || selected.Edition != pending.Edition || selected.Map != pending.Map || selected.ClusterId != pending.ClusterId)
                    throw new InvalidOperationException("The installation changed after its password was saved. Review the pending change.");
            }
        }
        finally { gate.Release(); }
    }

    public async Task DiscardAsync(Guid serverId, Guid pendingId, bool confirmed, CancellationToken ct = default)
    {
        if (!confirmed || serverId == Guid.Empty || pendingId == Guid.Empty) throw new ArgumentException("Confirm the reviewed saved password change.");
        await gate.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);
            var pending = document.Changes.SingleOrDefault(p => p.ServerId == serverId && p.Id == pendingId)
                ?? throw new InvalidOperationException("The saved password change no longer matches this review.");
            if (pending.Started) throw new InvalidOperationException("Password application has begun. Its recovery record cannot be discarded.");
            await store.SaveAsync(document with { Changes = document.Changes.Where(p => p.Id != pendingId).ToList() }, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<PendingServerAccessDocument> LoadAsync(CancellationToken ct)
    {
        var document = await store.LoadAsync(ct) ?? new(1, []);
        if (document.SchemaVersion != 1 || document.Changes is null || document.Changes.Any(p => p is null || p.Id == Guid.Empty || p.ServerId == Guid.Empty
            || !Path.IsPathFullyQualified(p.Directory) || !Enum.IsDefined(p.Edition) || string.IsNullOrWhiteSpace(p.Map)
            || p.AdministratorPassword is null && p.JoinPassword is null || !Hash(p.AdministratorSourceHash) || !Hash(p.JoinSourceHash))
            || document.Changes.Select(p => p.ServerId).Distinct().Count() != document.Changes.Count
            || document.Changes.Select(p => p.Id).Distinct().Count() != document.Changes.Count)
            throw new InvalidDataException("Invalid saved access changes.");
        foreach (var pending in document.Changes)
            ServerAccessConfiguration.ValidatePasswords(pending.AdministratorPassword ?? "unchanged", pending.JoinPassword ?? "");
        return document;
    }
    private static (string Admin, string Join) Credentials(ServerConfigurationFile source)
    {
        if (source.Text is null || !Hash(source.SourceSHA256)) throw new InvalidDataException("The server access configuration is missing.");
        var ini = new IniDocument(source.Text);
        return (Value("ServerAdminPassword"), Value("ServerPassword"));
        string Value(string key)
        {
            var values = ini.GetValues("ServerSettings", key);
            if (values.Count > 1) throw new InvalidDataException("Resolve repeated password entries in the advanced editor before changing access.");
            return values.SingleOrDefault() ?? "";
        }
    }
    private static void RequireTarget(string current, string? desired, string sourceHash)
    {
        if (desired is not null && Fingerprint(current) != sourceHash && current != desired)
            throw new InvalidOperationException("A targeted password changed after saving. Its queued change was retained for review.");
    }
    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);
}
