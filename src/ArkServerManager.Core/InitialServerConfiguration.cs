using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArkServerManager.Core;

/// <summary>Publishes explicitly prepared INI content only into a recorded, stopped new setup.</summary>
public static class InitialServerConfiguration
{
    private sealed record Receipt(int SchemaVersion, Guid ServerId, string UserSettingsSHA256, string GameSettingsSHA256);
    public static async Task WriteAsync(string managementRoot, Guid serverId, string userSettings, string gameSettings,
        Func<ServerInstallationRecord, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedSetup,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(withStoppedSetup);
        var encoding = new UTF8Encoding(false, true);
        var userBytes = Encode(userSettings); var gameBytes = Encode(gameSettings);
        var expected = new Receipt(1, serverId, Convert.ToHexString(SHA256.HashData(userBytes)), Convert.ToHexString(SHA256.HashData(gameBytes)));
        var record = await RequireReady(ct);
        var invoked = false;
        await withStoppedSetup(record, async token =>
        {
            if (invoked) throw new InvalidOperationException("Configuration setup cannot run twice.");
            invoked = true;
            var journal = Path.Combine(Path.GetFullPath(managementRoot), "app-settings", "creation", serverId.ToString("N") + ".json.lock");
            RejectLinks(journal);
            await using var ownership = new FileStream(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (await RequireReady(token) != record) throw new InvalidOperationException("The selected setup changed.");
            var parent = Path.Combine(record.StagingDirectory, "ShooterGame", "Saved", "Config");
            var target = Path.Combine(parent, SteamCmdInstaller.PayloadPlatform(record.Edition, record.Platform) + "Server");
            RejectLinks(target);
            if (Directory.Exists(target))
            {
                var receiptPath = Path.Combine(target, ".ark-config.json");
                RejectLinks(receiptPath);
                if (!File.Exists(receiptPath)) throw new InvalidOperationException("Existing configuration was not prepared by this setup. Review it before replacing it.");
                var saved = JsonSerializer.Deserialize<Receipt>(await ReadBounded(receiptPath, 65536, token),
                    new JsonSerializerOptions { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
                if (saved != expected) throw new InvalidOperationException("The prepared configuration differs from these choices. Review it before replacing it.");
                foreach (var item in new[] { ("GameUserSettings.ini", userBytes), ("Game.ini", gameBytes) })
                {
                    var path = Path.Combine(target, item.Item1); RejectLinks(path);
                    RequirePrivate(path);
                    if (!(await ReadBounded(path, 4 * 1024 * 1024, token)).AsSpan().SequenceEqual(item.Item2))
                        throw new InvalidOperationException("The prepared configuration changed outside this setup. Review it before continuing.");
                }
                return;
            }
            if (File.Exists(target))
                throw new InvalidOperationException("This setup already contains configuration. Review it before replacing it.");
            Directory.CreateDirectory(parent);
            RejectLinks(parent);
            var temporary = Path.Combine(parent, ".ark-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                await WritePrivate(Path.Combine(temporary, "GameUserSettings.ini"), userBytes, token);
                await WritePrivate(Path.Combine(temporary, "Game.ini"), gameBytes, token);
                await WritePrivate(Path.Combine(temporary, ".ark-config.json"), JsonSerializer.SerializeToUtf8Bytes(expected), token);
                RejectLinks(target); RejectLinks(temporary);
                if (await RequireReady(token) != record) throw new InvalidOperationException("The selected setup changed.");
                token.ThrowIfCancellationRequested();
                Directory.Move(temporary, target);
            }
            finally
            {
                // Cleanup is limited to this operation's fresh directory and known files.
                if (Directory.Exists(temporary))
                {
                    RejectLinks(temporary);
                    foreach (var name in new[] { "GameUserSettings.ini", "Game.ini", ".ark-config.json" })
                    { var path = Path.Combine(temporary, name); RejectLinks(path); if (File.Exists(path)) File.Delete(path); }
                    Directory.Delete(temporary, false);
                }
            }
        }, ct);
        if (!invoked) throw new InvalidOperationException("The stopped-setup scope did not authorize configuration.");

        byte[] Encode(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (text.Contains('\0') || encoding.GetByteCount(text) > 4 * 1024 * 1024)
                throw new ArgumentException("Configuration contains invalid content or exceeds the size limit.");
            return encoding.GetBytes(text);
        }
        async Task<ServerInstallationRecord> RequireReady(CancellationToken token)
        {
            var selected = (await ServerInstallationPreparation.ReadRecordedAsync(managementRoot, serverId, token)).Record;
            if (selected is null || selected.Phase != ServerInstallationPhase.FilesReady
                || !Directory.Exists(selected.StagingDirectory) || Directory.Exists(selected.FinalDirectory) || File.Exists(selected.FinalDirectory))
                throw new InvalidOperationException("Choose an unpublished setup with installed server files.");
            RejectLinks(selected.StagingDirectory); RejectLinks(selected.FinalDirectory);
            return selected;
        }
    }

    /// <summary>Read-only verification, used inside the caller's stopped-file operation scope.</summary>
    public static async Task ValidatePreparedAsync(string configurationDirectory, Guid serverId, CancellationToken ct = default)
    {
        if (serverId == Guid.Empty || !Path.IsPathFullyQualified(configurationDirectory))
            throw new ArgumentException("Choose a prepared configuration location.");
        var receiptPath = Path.Combine(configurationDirectory, ".ark-config.json");
        RejectLinks(receiptPath); RequirePrivate(receiptPath);
        var receipt = JsonSerializer.Deserialize<Receipt>(await ReadBounded(receiptPath, 65536, ct),
            new JsonSerializerOptions { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
        if (receipt is null || receipt.SchemaVersion != 1 || receipt.ServerId != serverId
            || !ValidHash(receipt.UserSettingsSHA256) || !ValidHash(receipt.GameSettingsSHA256))
            throw new InvalidDataException("Prepared configuration ownership could not be verified.");
        foreach (var item in new[] { ("GameUserSettings.ini", receipt.UserSettingsSHA256), ("Game.ini", receipt.GameSettingsSHA256) })
        {
            var path = Path.Combine(configurationDirectory, item.Item1); RejectLinks(path); RequirePrivate(path);
            if (Convert.ToHexString(SHA256.HashData(await ReadBounded(path, 4 * 1024 * 1024, ct))) != item.Item2)
                throw new InvalidDataException("Prepared configuration changed after preparation.");
        }
        static bool ValidHash(string? value) => value?.Length == 64 && value.All(char.IsAsciiHexDigit);
    }

    private static async Task<byte[]> ReadBounded(string path, int maximum, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum) throw new InvalidDataException("Configuration recovery file exceeds its size limit.");
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, ct); return bytes;
    }

    private static void RequirePrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
                throw new IOException("Configuration file permissions changed. Review access before continuing.");
            return;
        }
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        var allowed = new HashSet<string> { WindowsIdentity.GetCurrent().User!.Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value };
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (!security.AreAccessRulesProtected || rules.Length != allowed.Count)
            throw new IOException("Configuration file permissions changed. Review access before continuing.");
        foreach (var rule in rules)
            if (rule.IsInherited || rule.AccessControlType != AccessControlType.Allow
                || rule.FileSystemRights != FileSystemRights.FullControl || !allowed.Remove(rule.IdentityReference.Value))
                throw new IOException("Configuration file permissions changed. Review access before continuing.");
    }

    internal static async Task WritePrivate(string path, byte[] bytes, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
            Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        FileStream output;
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            output = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough, security);
        }
        else output = new FileStream(path, options);
        await using (output)
        { await output.WriteAsync(bytes, ct); await output.FlushAsync(ct); output.Flush(true); }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked configuration locations require separate validation.");
    }
}
