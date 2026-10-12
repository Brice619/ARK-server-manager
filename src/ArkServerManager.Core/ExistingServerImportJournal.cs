using System.Collections.Concurrent;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ExistingServerImportRecord(int SchemaVersion, ExistingServerTransferSelection Selection,
    ServerDefinition Server, InstallationTransferProof? Proof);
public sealed record ExistingServerImportStatus(Guid ServerId, string Name, InstallationImportMode Mode,
    string Source, string Destination, bool HasPublicationEvidence);
public sealed record ExistingServerImportRecoveryRequest(Guid ServerId, bool Confirmed);

/// <summary>Private confirmed intent and content evidence for imports that have not completed registration.</summary>
public sealed class ExistingServerImportJournal(string managementRoot)
{
    private readonly string root = Path.GetFullPath(managementRoot);
    private string Folder => Path.Combine(root, "app-settings", "existing-imports");
    private string PathFor(Guid id) => Path.Combine(Folder, id.ToString("N") + ".json");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private SemaphoreSlim Gate => Gates.GetOrAdd(root, _ => new(1, 1));

    public async Task AcceptAsync(ExistingServerImportRecord record, CancellationToken ct = default)
    {
        var snapshot = JsonSerializer.Deserialize<ExistingServerImportRecord>(JsonSerializer.SerializeToUtf8Bytes(record))!;
        Validate(snapshot);
        await Gate.WaitAsync(ct);
        try
        {
            var pending = await ReadCoreAsync(ct);
            if (pending.Any(existing => existing.Server.Id == snapshot.Server.Id
                || Overlap(existing.Selection.Registration.Expected.Directory, snapshot.Selection.Registration.Expected.Directory)
                || Overlap(existing.Server.Directory, snapshot.Server.Directory)
                || Overlap(existing.Server.Directory, snapshot.Selection.Registration.Expected.Directory)
                || Overlap(existing.Selection.Registration.Expected.Directory, snapshot.Server.Directory)))
                throw new InvalidOperationException("An import is already recorded for these files. Review its recovery before another import.");
            InstallationTransferProofs.CheckAncestors(Folder);
            await new AtomicJsonStore<ExistingServerImportRecord>(PathFor(snapshot.Server.Id), true).SaveAsync(snapshot, ct);
        }
        finally { Gate.Release(); }
    }

    public async Task SaveProofAsync(Guid id, InstallationTransferProof proof, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var record = (await ReadCoreAsync(ct)).SingleOrDefault(r => r.Server.Id == id)
                ?? throw new InvalidOperationException("The confirmed import record is missing.");
            var updated = record with { Proof = proof }; Validate(updated);
            await new AtomicJsonStore<ExistingServerImportRecord>(PathFor(id), true).SaveAsync(updated, ct);
        }
        finally { Gate.Release(); }
    }

    public async Task<IReadOnlyList<ExistingServerImportRecord>> ReadAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try { return await ReadCoreAsync(ct); }
        finally { Gate.Release(); }
    }

    public async Task RemoveAsync(ExistingServerImportRecord expected, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var current = (await ReadCoreAsync(ct)).SingleOrDefault(r => r.Server.Id == expected.Server.Id);
            if (current is null) return;
            if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(expected))
                throw new InvalidOperationException("The confirmed import changed before cleanup.");
            File.Delete(PathFor(expected.Server.Id));
        }
        finally { Gate.Release(); }
    }

    public async Task RemoveDeletedAsync(CatalogDocument catalog, ServerDeletionRecord deletion, CancellationToken ct = default)
    {
        DeletedServerRecordCleanup.RequireCompleted(catalog, deletion);
        foreach (var record in (await ReadAsync(ct)).Where(r => r.Server.Id == deletion.ServerId))
        {
            DeletedServerRecordCleanup.RequireDirectory(deletion, record.Server.Directory);
            await RemoveAsync(record, ct);
        }
    }

    private async Task<List<ExistingServerImportRecord>> ReadCoreAsync(CancellationToken ct)
    {
        InstallationTransferProofs.CheckAncestors(Folder);
        if (!Directory.Exists(Folder)) return [];
        var paths = Directory.GetFiles(Folder, "*.json");
        if (paths.Length > 1024) throw new InvalidDataException("Too many pending import records.");
        var records = new List<ExistingServerImportRecord>();
        foreach (var path in paths)
        {
            InstallationTransferProofs.CheckAncestors(path);
            if (new FileInfo(path).Length > 256 * 1024 * 1024) throw new InvalidDataException("An import record is too large.");
            var record = await new AtomicJsonStore<ExistingServerImportRecord>(path, true).LoadAsync(ct)
                ?? throw new InvalidDataException("An import record disappeared.");
            Validate(record);
            if (Path.GetFileName(path) != record.Server.Id.ToString("N") + ".json") throw new InvalidDataException("Import record identity mismatch.");
            records.Add(record);
        }
        return records;
    }

    private void Validate(ExistingServerImportRecord record)
    {
        if (record.SchemaVersion != 1 || record.Selection is null || record.Server is null
            || record.Selection.Registration is not { Confirmed: true } registration || registration.Expected is null
            || !record.Selection.TransferConfirmed || record.Selection.Mode is not (InstallationImportMode.Copy or InstallationImportMode.Move)
            || record.Server.Id == Guid.Empty || record.Server.Id != registration.ServerId
            || record.Server.Directory != record.Selection.Destination || record.Server.Edition != registration.Expected.Edition
            || record.Server.Map != registration.Map || record.Server.ClusterId is not null)
            throw new InvalidDataException("Invalid confirmed import record.");
        record.Server.Settings.Validate(); ServerMapIdentifier.ForLaunch(record.Server.Edition, record.Server.Map);
        AsaModLaunchArguments.Build(record.Server.Mods);
        var relative = Path.GetRelativePath(root, record.Server.Directory);
        if (relative == "." || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part == "..")
            || new[] { "app", "app-settings", "Dependencies", "Clusters" }.Contains(relative.Split('/', '\\')[0], StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Import destination is outside dedicated managed server storage.");
        if (record.Proof is { } proof)
        {
            InstallationTransferProofs.Validate(proof);
            if (proof.Source != registration.Expected.Directory || proof.Destination != record.Server.Directory)
                throw new InvalidDataException("Import file evidence does not match the confirmed request.");
        }
    }
    private static bool Overlap(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)); second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return first.Equals(second, comparison) || first.StartsWith(second + Path.DirectorySeparatorChar, comparison)
            || second.StartsWith(first + Path.DirectorySeparatorChar, comparison);
    }
}
