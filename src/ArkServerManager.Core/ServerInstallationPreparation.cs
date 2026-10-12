using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArkServerManager.Core;

public enum ServerInstallationPhase { Prepared, Downloading, FilesReady, Failed }
public sealed record ServerInstallationRecord(int SchemaVersion, Guid ServerId, ArkEdition Edition,
    ServerPlatform Platform, string StagingDirectory, string FinalDirectory, ServerInstallationPhase Phase);
public sealed record ServerInstallationStatus(ServerInstallationRecord? Record);
public sealed record ServerInstallationRecordInventory(IReadOnlyList<ServerInstallationRecord> Records, int UnreadableRecords);

/// <summary>Reuses shared game files in a separate server staging folder; downloads a missing game package once.</summary>
public sealed class ServerInstallationPreparation(CatalogRepository repository,
    Func<SteamInstallRequest, IProgress<IReadOnlyList<ProcessLine>>?, CancellationToken, Task<SteamInstallResult>> install)
{
    public ServerInstallationPreparation(CatalogRepository repository, SteamCmdInstaller installer)
        : this(repository, installer.InstallOrUpdateAsync) { }

    public static async Task<ServerInstallationRecordInventory> ReadInventoryAsync(string managementRoot, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(managementRoot)) throw new ArgumentException("Choose an installed management folder.");
        var directory = Path.Combine(Path.GetFullPath(managementRoot), "app-settings", "creation");
        RejectLinks(directory);
        if (!Directory.Exists(directory)) return new([], 0);
        var records = new List<ServerInstallationRecord>(); var unreadable = 0; var count = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 1024) throw new InvalidDataException("Too many installation records. Review recorded setups.");
            try
            {
                var name = Path.GetFileName(path);
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)
                    || name != id.ToString("N") + ".json") throw new InvalidDataException("Invalid installation record filename.");
                var status = await ReadRecordedAsync(managementRoot, id, ct);
                if (status.Record is not null) records.Add(status.Record);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { unreadable++; }
        }
        return new(records.OrderBy(r => r.ServerId).ToArray(), unreadable);
    }

    public static async Task<ServerInstallationStatus> ReadRecordedAsync(string managementRoot, Guid serverId, CancellationToken ct = default)
    {
        if (serverId == Guid.Empty || !Path.IsPathFullyQualified(managementRoot)) throw new ArgumentException("Choose a recorded setup.");
        var root = Path.GetFullPath(managementRoot);
        var journal = Path.Combine(root, "app-settings", "creation", serverId.ToString("N") + ".json");
        RejectLinks(journal);
        if (!File.Exists(journal)) return new(null);
        var record = await ReadRecordAsync(journal, ct);
        if (record.SchemaVersion != 1 || record.ServerId != serverId || !Enum.IsDefined(record.Edition)
            || !Enum.IsDefined(record.Platform) || !Enum.IsDefined(record.Phase)
            || record.StagingDirectory != Path.Combine(root, "servers", ".ark-create-" + serverId.ToString("N"))
            || record.FinalDirectory != Path.Combine(root, "servers", serverId.ToString("N")))
            throw new InvalidDataException("Recorded installation identity or locations are invalid.");
        return new(record);
    }

    public async Task<ServerInstallationRecord> PrepareAsync(string managementRoot, Guid serverId, ArkEdition edition,
        ServerPlatform platform, bool installationConsent, IProgress<IReadOnlyList<ProcessLine>>? progress = null,
        CancellationToken ct = default)
    {
        if (!installationConsent) throw new InvalidOperationException("Server-file installation requires confirmation.");
        if (serverId == Guid.Empty || !Enum.IsDefined(edition) || !Enum.IsDefined(platform)
            || !Path.IsPathFullyQualified(managementRoot) || !Directory.Exists(managementRoot))
            throw new ArgumentException("Choose a valid new server setup and installed management folder.");
        var parent = Path.Combine(Path.GetFullPath(managementRoot), "servers");
        var staging = Path.Combine(parent, ".ark-create-" + serverId.ToString("N"));
        var final = Path.Combine(parent, serverId.ToString("N"));
        var journal = Path.Combine(Path.GetFullPath(managementRoot), "app-settings", "creation", serverId.ToString("N") + ".json");
        var request = new SteamInstallRequest(edition, platform, staging, true);
        SteamCmdInstaller.Arguments(request); // Reject unsupported package/platform combinations before creating files.
        RejectLinks(staging); RejectLinks(final); RejectLinks(journal);
        if (Directory.Exists(staging) || File.Exists(staging) || Directory.Exists(final) || File.Exists(final) || File.Exists(journal))
            throw new InvalidOperationException("This setup already has files or recorded installation state. Review it before retrying.");
        var catalog = await repository.ReadAsync(ct);
        if (catalog.Servers.Any(s => s.Id == serverId || Overlap(staging, s.Directory) || Overlap(final, s.Directory)))
            throw new InvalidOperationException("The new setup overlaps an existing server.");
        Directory.CreateDirectory(parent);
        RejectLinks(parent);
        // The operation lock serializes independent callers before either creates staging/journal state.
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        RejectLinks(journal + ".lock");
        await using var ownership = new FileStream(journal + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        RejectLinks(staging); RejectLinks(final); RejectLinks(journal);
        if (Directory.Exists(staging) || File.Exists(staging) || Directory.Exists(final) || File.Exists(final) || File.Exists(journal))
            throw new InvalidOperationException("Installation was already started for this setup.");
        var store = new AtomicJsonStore<ServerInstallationRecord>(journal);
        var record = new ServerInstallationRecord(1, serverId, edition, platform, staging, final, ServerInstallationPhase.Prepared);
        await store.SaveAsync(record, ct);
        return await InstallOwnedAsync(record, store, progress, ct);
    }

    public async Task<ServerInstallationRecord> ResumeAsync(string managementRoot, Guid serverId, bool installationConsent,
        Func<ServerInstallationRecord, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedFilesAndInstaller,
        IProgress<IReadOnlyList<ProcessLine>>? progress = null, CancellationToken ct = default)
    {
        if (!installationConsent) throw new InvalidOperationException("Server-file installation requires confirmation.");
        ArgumentNullException.ThrowIfNull(withStoppedFilesAndInstaller);
        if (serverId == Guid.Empty || !Path.IsPathFullyQualified(managementRoot) || !Directory.Exists(managementRoot))
            throw new ArgumentException("Choose a recorded setup in the installed management folder.");
        var root = Path.GetFullPath(managementRoot);
        var journal = Path.Combine(root, "app-settings", "creation", serverId.ToString("N") + ".json");
        RejectLinks(journal); RejectLinks(journal + ".lock");
        await using var ownership = new FileStream(journal + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var record = await ReadRecordAsync(journal, ct);
        if (record.SchemaVersion != 1 || record.ServerId != serverId || !Enum.IsDefined(record.Edition)
            || !Enum.IsDefined(record.Platform) || !Enum.IsDefined(record.Phase)
            || record.StagingDirectory != Path.Combine(root, "servers", ".ark-create-" + serverId.ToString("N"))
            || record.FinalDirectory != Path.Combine(root, "servers", serverId.ToString("N")))
            throw new InvalidDataException("The recorded installation locations or identity changed. Review this setup.");
        SteamCmdInstaller.Arguments(new(record.Edition, record.Platform, record.StagingDirectory, true));
        var invoked = false;
        ServerInstallationRecord? result = null;
        await withStoppedFilesAndInstaller(record, async token =>
        {
            if (invoked) throw new InvalidOperationException("Installation resume was authorized more than once.");
            invoked = true;
            if (await ReadRecordAsync(journal, token) != record) throw new InvalidOperationException("Installation state changed. Review it again.");
            RejectLinks(record.StagingDirectory); RejectLinks(record.FinalDirectory); RejectLinks(journal);
            if (File.Exists(record.StagingDirectory) || File.Exists(record.FinalDirectory) || Directory.Exists(record.FinalDirectory))
                throw new IOException("Installation locations no longer match the staged setup.");
            if (Directory.Exists(record.StagingDirectory)) RejectTreeLinks(record.StagingDirectory);
            var catalog = await repository.ReadAsync(token);
            if (catalog.Servers.Any(s => s.Id == serverId || Overlap(record.StagingDirectory, s.Directory) || Overlap(record.FinalDirectory, s.Directory)))
                throw new InvalidOperationException("The staged setup overlaps a registered server.");
            if (record.Phase == ServerInstallationPhase.FilesReady)
            { ValidatePackage(record); result = record; }
            else result = await InstallOwnedAsync(record, new(journal), progress, token);
        }, ct);
        return result ?? throw new InvalidOperationException("Stopped-file and installer ownership checks did not authorize resume.");
    }

    private async Task<ServerInstallationRecord> InstallOwnedAsync(ServerInstallationRecord record,
        AtomicJsonStore<ServerInstallationRecord> store, IProgress<IReadOnlyList<ProcessLine>>? progress, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(record.StagingDirectory);
            record = record with { Phase = ServerInstallationPhase.Downloading };
            await store.SaveAsync(record, ct);
            var request = new SteamInstallRequest(record.Edition, record.Platform, record.StagingDirectory, true);
            var sharedFiles = new SharedServerFiles(Path.GetDirectoryName(Path.GetDirectoryName(record.FinalDirectory))!, record.Edition, record.Platform);
            var reused = await sharedFiles.AdoptExistingAsync(repository, record.StagingDirectory, ct);
            var result = reused ? new SteamInstallResult(SteamCmdInstaller.AppId(record.Edition), record.StagingDirectory, 0, true, "Reused installed game files.")
                : await install(request, progress, ct);
            if (!result.Success || result.ExitCode != 0 || result.AppId != SteamCmdInstaller.AppId(record.Edition)
                || !Path.GetFullPath(result.Directory).Equals(record.StagingDirectory, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("The server package did not confirm installation completion.");
            ValidatePackage(record);
            if (!reused) await sharedFiles.RegisterAsync(record.StagingDirectory, ct);
            ct.ThrowIfCancellationRequested();
            record = record with { Phase = ServerInstallationPhase.FilesReady };
            await store.SaveAsync(record, ct);
            return record;
        }
        catch
        {
            // Keep partial downloaded files for explicit recovery; never remove files from another setup.
            await store.SaveAsync(record with { Phase = ServerInstallationPhase.Failed }, CancellationToken.None);
            throw;
        }
    }

    private static void ValidatePackage(ServerInstallationRecord record)
    {
        RejectTreeLinks(record.StagingDirectory);
        var candidate = new InstallationDiscovery().Evaluate(record.StagingDirectory).Candidates
            .SingleOrDefault(c => c.Edition == record.Edition && c.Platform == SteamCmdInstaller.PayloadPlatform(record.Edition, record.Platform).ToString());
        if (candidate is null || new FileInfo(candidate.Executable).Length == 0)
            throw new IOException("The installed package does not contain the expected server executable.");
    }

    private static async Task<ServerInstallationRecord> ReadRecordAsync(string path, CancellationToken ct)
    {
        RejectLinks(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int read;
        while ((read = await file.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + read > 64 * 1024) throw new InvalidDataException("Installation state exceeds the supported size.");
            buffer.Write(bytes, 0, read);
        }
        return JsonSerializer.Deserialize<ServerInstallationRecord>(buffer.ToArray(), new JsonSerializerOptions
            { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Installation state is empty.");
    }

    private static bool Overlap(string a, string b)
    {
        a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)); b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return a.Equals(b, comparison) || a.StartsWith(b + Path.DirectorySeparatorChar, comparison)
            || b.StartsWith(a + Path.DirectorySeparatorChar, comparison);
    }
    private static void RejectTreeLinks(string directory)
    {
        RejectLinks(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        { RejectLinks(entry); if (Directory.Exists(entry)) RejectTreeLinks(entry); }
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked installation locations require review.");
    }
}
