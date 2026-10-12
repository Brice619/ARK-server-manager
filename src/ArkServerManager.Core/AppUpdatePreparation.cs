namespace ArkServerManager.Core;

public enum AppUpdatePhase { Prepared, Applying, Applied, RollingBack, RolledBack, Confirmed }
public sealed record AppUpdateRecord(int SchemaVersion, Guid Id, string ApplicationDirectory, string PayloadDirectory,
    string RecoveryDirectory, InstallerPayload Previous, InstallerPayload Replacement, AppUpdatePhase Phase)
{
    public PayloadFile? PreviousManifest { get; init; }
    public PayloadFile? ReplacementManifest { get; init; }
}

/// <summary>Captures only installed app-owned files before a stopped-app replacement may begin.</summary>
public static class AppUpdatePreparation
{
    public static async Task<AppUpdateRecord> PrepareAsync(string applicationDirectory, string payloadDirectory, string recoveryParent,
        string runtime, Func<Func<CancellationToken, Task>, CancellationToken, Task> withStoppedApplication, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(recoveryParent) || !Directory.Exists(recoveryParent))
            throw new ArgumentException("An existing absolute update recovery folder is required.");
        RejectLinks(recoveryParent);
        var installed = await VerifiedPayloadInstaller.VerifyInstalledAsync(applicationDirectory, runtime, ct);
        var replacement = await VerifiedPayloadInstaller.VerifyAsync(payloadDirectory, runtime, ct);
        var app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        var payload = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadDirectory));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recoveryParent));
        if (Contains(app, payload) || Contains(payload, app) || Contains(app, parent) || Contains(payload, parent))
            throw new ArgumentException("Update payload and recovery must be outside the installed application.");
        foreach (var file in installed.Files.Concat(replacement.Files)) ValidateOwnedPath(file.Path);
        var id = Guid.NewGuid();
        var recovery = Path.Combine(parent, ".ark-rollback-" + id.ToString("N"));
        var invoked = false;
        AppUpdateRecord? prepared = null;
        await withStoppedApplication(async token =>
        {
            if (invoked) throw new InvalidOperationException("The update preparation scope was invoked twice.");
            invoked = true;
            var current = await VerifiedPayloadInstaller.VerifyInstalledAsync(app, runtime, token);
            var fresh = await VerifiedPayloadInstaller.VerifyAsync(payload, runtime, token);
            if (!Equivalent(current, installed) || !Equivalent(fresh, replacement))
                throw new InvalidOperationException("The app or update payload changed during preparation.");
            foreach (var file in replacement.Files)
            {
                var target = Path.Combine(app, file.Path); RejectLinks(target);
                if (Directory.Exists(target) || File.Exists(target) && !installed.Files.Any(f => string.Equals(f.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("An update would replace a file that is not owned by the installed app.");
            }
            Directory.CreateDirectory(recovery);
            foreach (var file in installed.Files)
            {
                token.ThrowIfCancellationRequested();
                var source = Path.Combine(app, file.Path); RejectLinks(source);
                var target = Path.Combine(recovery, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                {
                    await input.CopyToAsync(output, token);
                    await output.FlushAsync(token); output.Flush(true);
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(source));
            }
            File.Copy(Path.Combine(app, "payload-manifest.json"), Path.Combine(recovery, "payload-manifest.json"), overwrite: false);
            await VerifiedPayloadInstaller.VerifyAsync(recovery, runtime, token);
            prepared = new(1, id, app, payload, recovery, installed, replacement, AppUpdatePhase.Prepared)
            {
                PreviousManifest = await ManifestIdentityAsync(recovery, token),
                ReplacementManifest = await ManifestIdentityAsync(payload, token)
            };
            RejectLinks(Path.Combine(recovery, "update.json"));
            await new AtomicJsonStore<AppUpdateRecord>(Path.Combine(recovery, "update.json")).SaveAsync(prepared, token);
        }, ct);
        if (!invoked || prepared is null) throw new InvalidOperationException("The stopped-app scope did not prepare the update.");
        return prepared;
    }

    internal static void ValidateOwnedPath(string relative)
    {
        var first = relative.Split('/')[0];
        if (new[] { "Servers", "Clusters", "GameFiles", "Dependencies", "State", "app-settings", "logs", "catalog.json", "installation.json", AppFileLease.FileName, AppBootstrapLayout.FolderName, AppBootstrapLayout.PayloadFolderName }
            .Contains(first, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Application update ownership includes managed data.");
    }
    private static async Task<PayloadFile> ManifestIdentityAsync(string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, "payload-manifest.json"); RejectLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new("payload-manifest.json", input.Length,
            Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, ct)));
    }
    internal static bool Equivalent(InstallerPayload left, InstallerPayload right) => left.SchemaVersion == right.SchemaVersion
        && left.Runtime == right.Runtime && left.SelfContained == right.SelfContained && left.Files.SequenceEqual(right.Files);
    private static bool Contains(string parent, string path) => path.Equals(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || path.StartsWith(parent + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked app update locations are unsupported.");
    }
}
