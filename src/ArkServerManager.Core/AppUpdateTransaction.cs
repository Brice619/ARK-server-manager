using System.Security.Cryptography;
using System.Text.Json;
using static ArkServerManager.Core.AppUpdatePreparation;

namespace ArkServerManager.Core;

/// <summary>App-file transaction, invoked by an independent updater under a verified stopped-app scope.</summary>
public static class AppUpdateTransaction
{
    public static async Task<AppUpdateRecord> ConfirmAsync(string journal, string applicationDirectory,
        Func<CancellationToken, Task<bool>> verifyStartup, CancellationToken ct = default)
    {
        var record = await ReadAsync(journal, ct);
        if (!SamePath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory)), record.ApplicationDirectory))
            throw new InvalidDataException("Startup confirmation belongs to another installed application.");
        var lockPath = Path.Combine(record.RecoveryDirectory, "update.lock"); RejectLinks(lockPath);
        await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        record = await ReadAsync(journal, ct);
        if (record.Phase is not (AppUpdatePhase.Applied or AppUpdatePhase.Confirmed))
            throw new InvalidOperationException("Only an applied app update can be confirmed.");
        var installed = await VerifiedPayloadInstaller.VerifyInstalledAsync(record.ApplicationDirectory, record.Replacement.Runtime, ct);
        if (!Equivalent(installed, record.Replacement)) throw new InvalidDataException("The running installation does not match the applied update.");
        if (record.Phase == AppUpdatePhase.Confirmed || !await verifyStartup(ct)) return record;
        record = record with { Phase = AppUpdatePhase.Confirmed };
        await new AtomicJsonStore<AppUpdateRecord>(journal).SaveAsync(record, ct);
        return record;
    }

    public static Task<AppUpdateRecord> ApplyAsync(string journal,
        Func<Func<CancellationToken, Task>, CancellationToken, Task> withStoppedApplication, CancellationToken ct = default) =>
        ExecuteAsync(journal, withStoppedApplication, rollback: false, ct);

    public static Task<AppUpdateRecord> RestoreAsync(string journal,
        Func<Func<CancellationToken, Task>, CancellationToken, Task> withStoppedApplication, CancellationToken ct = default) =>
        ExecuteAsync(journal, withStoppedApplication, rollback: true, ct);

    private static async Task<AppUpdateRecord> ExecuteAsync(string journal,
        Func<Func<CancellationToken, Task>, CancellationToken, Task> scope, bool rollback, CancellationToken ct)
    {
        var record = await ReadAsync(journal, ct);
        var lockPath = Path.Combine(record.RecoveryDirectory, "update.lock"); RejectLinks(lockPath);
        await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        AppUpdateRecord? result = null; var invoked = false;
        await scope(async token =>
        {
            if (invoked) throw new InvalidOperationException("The stopped-app update scope was invoked twice.");
            invoked = true;
            record = await ReadAsync(journal, token);
            if (rollback)
            {
                if (record.Phase == AppUpdatePhase.Confirmed) throw new InvalidOperationException("This update was already confirmed.");
                result = await RestoreCoreAsync(journal, record, token);
                return;
            }
            if (record.Phase == AppUpdatePhase.Applied)
            {
                var applied = await VerifiedPayloadInstaller.VerifyInstalledAsync(record.ApplicationDirectory, record.Replacement.Runtime, token);
                if (!Equivalent(applied, record.Replacement)) throw new InvalidDataException("The applied update no longer matches its payload.");
                result = record; return;
            }
            if (record.Phase != AppUpdatePhase.Prepared) throw new InvalidOperationException("Recover the interrupted app update before trying again.");
            var current = await VerifiedPayloadInstaller.VerifyInstalledAsync(record.ApplicationDirectory, record.Previous.Runtime, token);
            var fresh = await VerifiedPayloadInstaller.VerifyAsync(record.PayloadDirectory, record.Replacement.Runtime, token);
            if (!Equivalent(current, record.Previous) || !Equivalent(fresh, record.Replacement))
                throw new InvalidOperationException("The app or update payload changed after preparation.");
            foreach (var added in record.Replacement.Files.Where(n => !record.Previous.Files.Any(o => SamePath(o.Path, n.Path))))
            {
                var path = Path.Combine(record.ApplicationDirectory, added.Path); RejectLinks(path);
                if (File.Exists(path) || Directory.Exists(path))
                    throw new IOException("A new update file location became occupied after preparation. The existing file has been preserved.");
            }
            await VerifyTargetsAsync(record, token);
            var store = new AtomicJsonStore<AppUpdateRecord>(journal);
            record = record with { Phase = AppUpdatePhase.Applying };
            await store.SaveAsync(record, token);
            try
            {
                foreach (var file in record.Replacement.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var old = record.Previous.Files.FirstOrDefault(f => SamePath(f.Path, file.Path));
                    await RequireCurrentAsync(Path.Combine(record.ApplicationDirectory, file.Path), old, null, token);
                    await ReplaceAsync(Path.Combine(record.PayloadDirectory, file.Path), Path.Combine(record.ApplicationDirectory, file.Path), file, token);
                }
                foreach (var old in record.Previous.Files.Where(f => !record.Replacement.Files.Any(n => SamePath(n.Path, f.Path))))
                {
                    var path = Path.Combine(record.ApplicationDirectory, old.Path);
                    await RequireCurrentAsync(path, old, null, token);
                    File.Delete(path);
                }
                await RequireCurrentAsync(Path.Combine(record.ApplicationDirectory, "payload-manifest.json"), record.PreviousManifest, null, token);
                await ReplaceAsync(Path.Combine(record.PayloadDirectory, "payload-manifest.json"),
                    Path.Combine(record.ApplicationDirectory, "payload-manifest.json"), record.ReplacementManifest, token);
                await VerifiedPayloadInstaller.VerifyInstalledAsync(record.ApplicationDirectory, record.Replacement.Runtime, token);
                record = record with { Phase = AppUpdatePhase.Applied };
                await store.SaveAsync(record, token); result = record;
            }
            catch
            {
                // Cancellation must not interrupt recovery of already-replaced app files.
                await RestoreCoreAsync(journal, record, CancellationToken.None);
                throw;
            }
        }, ct);
        if (!invoked || result is null) throw new InvalidOperationException("The stopped-app scope did not run the update.");
        return result;
    }

    private static async Task<AppUpdateRecord> RestoreCoreAsync(string journal, AppUpdateRecord record, CancellationToken ct)
    {
        if (record.Phase is AppUpdatePhase.Prepared or AppUpdatePhase.RolledBack)
        {
            var unchanged = await VerifiedPayloadInstaller.VerifyInstalledAsync(record.ApplicationDirectory, record.Previous.Runtime, ct);
            if (!Equivalent(unchanged, record.Previous)) throw new InvalidDataException("The installed app changed outside this update.");
            record = record with { Phase = AppUpdatePhase.RolledBack };
            await new AtomicJsonStore<AppUpdateRecord>(journal).SaveAsync(record, ct);
            return record;
        }
        await VerifyTargetsAsync(record, ct);
        var store = new AtomicJsonStore<AppUpdateRecord>(journal);
        record = record with { Phase = AppUpdatePhase.RollingBack };
        await store.SaveAsync(record, ct);
        foreach (var old in record.Previous.Files)
        {
            var path = Path.Combine(record.ApplicationDirectory, old.Path);
            var replacement = record.Replacement.Files.FirstOrDefault(n => SamePath(n.Path, old.Path));
            await RequireCurrentAsync(path, old, replacement, ct, missingAllowed: true);
            if (File.Exists(path) && await MatchesAsync(path, old, ct)) continue;
            await ReplaceAsync(Path.Combine(record.RecoveryDirectory, old.Path), path, old, ct);
        }
        foreach (var added in record.Replacement.Files.Where(n => !record.Previous.Files.Any(o => SamePath(o.Path, n.Path))))
        {
            var path = Path.Combine(record.ApplicationDirectory, added.Path);
            await RequireCurrentAsync(path, null, added, ct, missingAllowed: true);
            if (File.Exists(path)) File.Delete(path);
        }
        await RequireCurrentAsync(Path.Combine(record.ApplicationDirectory, "payload-manifest.json"), record.PreviousManifest, record.ReplacementManifest, ct);
        await ReplaceAsync(Path.Combine(record.RecoveryDirectory, "payload-manifest.json"),
            Path.Combine(record.ApplicationDirectory, "payload-manifest.json"), record.PreviousManifest, ct);
        await VerifiedPayloadInstaller.VerifyInstalledAsync(record.ApplicationDirectory, record.Previous.Runtime, ct);
        record = record with { Phase = AppUpdatePhase.RolledBack };
        await store.SaveAsync(record, ct);
        return record;
    }

    private static async Task VerifyTargetsAsync(AppUpdateRecord record, CancellationToken ct)
    {
        await RequireCurrentAsync(Path.Combine(record.ApplicationDirectory, "payload-manifest.json"), record.PreviousManifest, record.ReplacementManifest, ct);
        foreach (var name in record.Previous.Files.Select(f => f.Path).Concat(record.Replacement.Files.Select(f => f.Path)).Distinct(StringComparer.OrdinalIgnoreCase))
            await RequireCurrentAsync(Path.Combine(record.ApplicationDirectory, name),
                record.Previous.Files.FirstOrDefault(f => SamePath(f.Path, name)),
                record.Replacement.Files.FirstOrDefault(f => SamePath(f.Path, name)), ct, missingAllowed: true);
    }

    private static async Task RequireCurrentAsync(string path, PayloadFile? previous, PayloadFile? replacement, CancellationToken ct, bool missingAllowed = false)
    {
        RejectLinks(path);
        if (Directory.Exists(path)) throw new IOException("An app file location is occupied by a folder.");
        if (!File.Exists(path))
        {
            if (missingAllowed || previous is null) return;
            throw new IOException("An installed app file is missing.");
        }
        if (await MatchesAsync(path, previous, ct) || await MatchesAsync(path, replacement, ct)) return;
        throw new IOException("An app file changed outside the update. It has been preserved for review.");
    }

    private static async Task<bool> MatchesAsync(string path, PayloadFile? file, CancellationToken ct)
    {
        if (file is null) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return input.Length == file.Bytes && Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(file.SHA256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ReplaceAsync(string source, string target, PayloadFile? expected, CancellationToken ct)
    {
        RejectLinks(source); RejectLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".ark-update-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true);
            }
            if (expected is not null && !await MatchesAsync(temporary, expected, ct)) throw new InvalidDataException("Update source file changed while copying.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(source));
            ct.ThrowIfCancellationRequested(); RejectLinks(target);
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target, overwrite: false);
        }
        finally { RejectLinks(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<AppUpdateRecord> ReadAsync(string journal, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(journal)) throw new ArgumentException("An absolute update journal is required.");
        RejectLinks(journal);
        await using var input = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("Update journal is too large.");
        var record = await JsonSerializer.DeserializeAsync<AppUpdateRecord>(input, cancellationToken: ct)
            ?? throw new InvalidDataException("Update journal is empty.");
        if (record.SchemaVersion != 1 || record.Id == Guid.Empty || !Enum.IsDefined(record.Phase)
            || record.Previous is null || record.Replacement is null
            || !Path.IsPathFullyQualified(record.ApplicationDirectory) || !Path.IsPathFullyQualified(record.PayloadDirectory)
            || !Path.IsPathFullyQualified(record.RecoveryDirectory)
            || Path.GetFileName(record.RecoveryDirectory) != ".ark-rollback-" + record.Id.ToString("N")
            || Path.GetFullPath(journal) != Path.Combine(record.RecoveryDirectory, "update.json"))
            throw new InvalidDataException("Invalid update recovery identity.");
        foreach (var manifest in new[] { record.PreviousManifest, record.ReplacementManifest })
            if (manifest is null || manifest.Path != "payload-manifest.json" || manifest.Bytes <= 0 || manifest.Bytes > 4 * 1024 * 1024
                || manifest.SHA256 is null || manifest.SHA256.Length != 64 || manifest.SHA256.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidDataException("Update recovery manifest identity is missing or invalid.");
        foreach (var path in new[] { record.ApplicationDirectory, record.PayloadDirectory, record.RecoveryDirectory }) RejectLinks(path);
        if (InstalledAppSettingsFile.Read(record.ApplicationDirectory) is null) throw new InvalidDataException("The installed app settings are missing.");
        var previous = await VerifiedPayloadInstaller.VerifyRecoveryAsync(record.RecoveryDirectory, record.Previous.Runtime, ct);
        if (!await MatchesAsync(Path.Combine(record.RecoveryDirectory, "payload-manifest.json"), record.PreviousManifest, ct))
            throw new InvalidDataException("The saved recovery manifest changed.");
        if (!Equivalent(previous, record.Previous) || record.Replacement.Runtime != record.Previous.Runtime)
            throw new InvalidDataException("Update recovery ownership does not match its saved payload.");
        // Recovery needs the verified prior payload even if the new payload was lost or corrupted.
        VerifiedPayloadInstaller.ValidateManifest(record.Replacement, record.Previous.Runtime);
        foreach (var file in previous.Files.Concat(record.Replacement.Files)) ValidateOwnedPath(file.Path);
        return record;
    }

    private static bool SamePath(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
