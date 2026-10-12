using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArkServerManager.Core;

public enum ServerCreationPublicationPhase { Prepared, Published, Registered }
public sealed record ServerCreationPublicationRecord(int SchemaVersion, Guid ServerId, string SelectionSHA256,
    string UserSettingsSHA256, string GameSettingsSHA256, ServerCreationPublicationPhase Phase);

/// <summary>Resumable publication under caller-owned stopped-file scope. Never starts a server.</summary>
public static class ServerCreationPublication
{
    public static async Task<ServerCreationPublicationRecord> ExecuteAsync(CatalogRepository repository, string managementRoot,
        ServerDefinition server, ClusterDefinition? newCluster,
        Func<ServerInstallationRecord, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedSetup,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(withStoppedSetup);
        server.Settings.Validate();
        ServerMapIdentifier.ForLaunch(server.Edition, server.Map);
        var installation = (await ServerInstallationPreparation.ReadRecordedAsync(managementRoot, server.Id, ct)).Record
            ?? throw new InvalidOperationException("Choose a recorded server setup.");
        if (installation.Phase != ServerInstallationPhase.FilesReady || installation.Edition != server.Edition
            || server.Directory != installation.FinalDirectory)
            throw new InvalidOperationException("Server setup does not match the installed package.");
        if (newCluster is not null && (newCluster.Id != server.ClusterId || newCluster.Edition != server.Edition))
            throw new ArgumentException("The new cluster must match this server.");
        var fingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(new { Server = server, NewCluster = newCluster }));
        var journal = Path.Combine(Path.GetFullPath(managementRoot), "app-settings", "creation-publication", server.Id.ToString("N") + ".json");
        var store = new AtomicJsonStore<ServerCreationPublicationRecord>(journal);
        ServerCreationPublicationRecord? result = null; var invoked = false;
        await withStoppedSetup(installation, async token =>
        {
            if (invoked) throw new InvalidOperationException("Creation publication cannot run twice.");
            invoked = true;
            var installationLock = Path.Combine(Path.GetFullPath(managementRoot), "app-settings", "creation", server.Id.ToString("N") + ".json.lock");
            RejectLinks(installationLock); RejectLinks(journal);
            await using var ownership = new FileStream(installationLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if ((await ServerInstallationPreparation.ReadRecordedAsync(managementRoot, server.Id, token)).Record != installation)
                throw new InvalidOperationException("The installation changed.");
            var record = await ReadRecord(journal, token);
            if (record is not null && (record.SchemaVersion != 1 || record.ServerId != server.Id || record.SelectionSHA256 != fingerprint
                || !Enum.IsDefined(record.Phase) || !ValidHash(record.UserSettingsSHA256) || !ValidHash(record.GameSettingsSHA256)))
                throw new InvalidDataException("Creation recovery does not match the reviewed setup.");
            await repository.ChangeAsync(async (catalog, commitToken) =>
            {
                var existing = catalog.Servers.SingleOrDefault(s => s.Id == server.Id);
                var staged = Directory.Exists(installation.StagingDirectory);
                var published = Directory.Exists(installation.FinalDirectory);
                RejectLinks(installation.StagingDirectory); RejectLinks(installation.FinalDirectory);
                if (File.Exists(installation.StagingDirectory) || File.Exists(installation.FinalDirectory) || staged == published)
                    throw new IOException("Creation folders require review before continuing.");
                if (existing is not null)
                {
                    if (record is null || staged || Hash(JsonSerializer.SerializeToUtf8Bytes(existing)) != Hash(JsonSerializer.SerializeToUtf8Bytes(server)))
                        throw new InvalidOperationException("The registered server does not match this creation.");
                    if (newCluster is not null && !catalog.Clusters.Any(c => c.Id == newCluster.Id
                        && Hash(JsonSerializer.SerializeToUtf8Bytes(c)) == Hash(JsonSerializer.SerializeToUtf8Bytes(newCluster))))
                        throw new InvalidOperationException("The registered cluster changed.");
                    await ValidateFiles(installation.FinalDirectory, record, commitToken);
                    result = record with { Phase = ServerCreationPublicationPhase.Registered };
                    return;
                }
                if (published && record is null) throw new IOException("The destination was not published by this setup.");
                if (staged && record?.Phase == ServerCreationPublicationPhase.Published)
                    throw new IOException("Recorded publication does not match the setup folders.");
                if (record?.Phase == ServerCreationPublicationPhase.Registered)
                    throw new InvalidOperationException("The registered creation was removed. Review it instead of recreating it.");
                foreach (var other in catalog.Servers)
                    if (Overlap(other.Directory, installation.StagingDirectory) || Overlap(other.Directory, installation.FinalDirectory))
                        throw new IOException("Creation overlaps a registered server.");
                // Validate the complete catalog mutation before any folder publication.
                var preview = new ServerCatalog();
                foreach (var cluster in catalog.Clusters) preview.AddCluster(cluster);
                foreach (var member in catalog.Servers) preview.AddServer(member);
                if (newCluster is not null) preview.AddCluster(newCluster);
                preview.AddServer(server);
                using var ports = new PortAllocator().ReserveExact(server.Settings, catalog.Servers.Select(s => s.Settings));
                var directory = staged ? installation.StagingDirectory : installation.FinalDirectory;
                record ??= await CaptureFiles(directory, commitToken);
                await ValidateFiles(directory, record, commitToken);
                RejectLinks(journal);
                await store.SaveAsync(record, commitToken);
                if (staged)
                {
                    commitToken.ThrowIfCancellationRequested();
                    Directory.Move(installation.StagingDirectory, installation.FinalDirectory);
                }
                record = record with { Phase = ServerCreationPublicationPhase.Published };
                await store.SaveAsync(record, commitToken);
                if (newCluster is not null) catalog.AddCluster(newCluster);
                catalog.AddServer(server);
                result = record;
            }, token);
            result = result! with { Phase = ServerCreationPublicationPhase.Registered };
            await store.SaveAsync(result, CancellationToken.None);
        }, ct);
        return result ?? throw new InvalidOperationException("The stopped-setup scope did not authorize publication.");

        async Task<ServerCreationPublicationRecord> CaptureFiles(string directory, CancellationToken token)
            => new(1, server.Id, fingerprint, await FileHash(Config(directory, "GameUserSettings.ini"), token),
                await FileHash(Config(directory, "Game.ini"), token), ServerCreationPublicationPhase.Prepared);
        async Task ValidateFiles(string directory, ServerCreationPublicationRecord record, CancellationToken token)
        {
            CheckTree(directory, token);
            var candidate = new InstallationDiscovery().Evaluate(directory).Candidates.SingleOrDefault(c => c.Edition == server.Edition
                && c.Platform == SteamCmdInstaller.PayloadPlatform(installation.Edition, installation.Platform).ToString());
            if (candidate is null || new FileInfo(candidate.Executable).Length == 0)
                throw new InvalidDataException("The expected server executable is missing.");
            await InitialServerConfiguration.ValidatePreparedAsync(Path.GetDirectoryName(Config(directory, "Game.ini"))!, server.Id, token);
            if (await FileHash(Config(directory, "GameUserSettings.ini"), token) != record.UserSettingsSHA256
                || await FileHash(Config(directory, "Game.ini"), token) != record.GameSettingsSHA256)
                throw new InvalidDataException("Prepared server configuration changed.");
            var text = await File.ReadAllTextAsync(Config(directory, "GameUserSettings.ini"), token);
            GuidedServerConfiguration.ValidateIdentity(server, text);
            ServerRconConfiguration.FromConfiguration(server, text, TimeSpan.FromSeconds(5), RconResponseMode.SinglePacket);
        }
        string Config(string directory, string file) => Path.Combine(directory, "ShooterGame", "Saved", "Config", installation.Platform + "Server", file);
    }

    private static async Task<ServerCreationPublicationRecord?> ReadRecord(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 65536) throw new InvalidDataException("Creation record exceeds its size limit.");
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<ServerCreationPublicationRecord>(bytes,
            new JsonSerializerOptions { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Creation record is empty.");
    }
    private static async Task<string> FileHash(string path, CancellationToken ct)
    {
        RejectLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("Configuration exceeds its size limit.");
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
    }
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static bool ValidHash(string? value) => value?.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static void CheckTree(string directory, CancellationToken ct)
    {
        RejectLinks(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        { ct.ThrowIfCancellationRequested(); RejectLinks(entry); if (Directory.Exists(entry)) CheckTree(entry, ct); }
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked creation locations require review.");
    }
    private static bool Overlap(string first, string second) => Contains(first, second) || Contains(second, first);
    private static bool Contains(string parent, string child)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(child));
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar));
    }
}
