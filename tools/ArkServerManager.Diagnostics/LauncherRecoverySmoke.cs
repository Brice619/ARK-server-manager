using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ArkServerManager.Core;

internal static class LauncherRecoverySmoke
{
    public static async Task<object> RunAsync(string launcher, string fixture, string evidence, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || new[] { launcher, fixture, evidence }.Any(path => !Path.IsPathFullyQualified(path)))
            throw new ArgumentException("Choose absolute Windows launcher, fixture and evidence paths.");
        if (Path.Exists(evidence)) throw new IOException("Choose a new evidence directory.");
        var payload = Path.Combine(evidence, "payload"); Directory.CreateDirectory(payload);
        foreach (var name in new[] { "ArkServerManager.Desktop.runtimeconfig.json", "System.Private.CoreLib.dll", "manager/ArkServerManager.Host.exe",
            "manager/ArkServerManager.Host.dll", "manager/ArkServerManager.Host.runtimeconfig.json", "manager/System.Private.CoreLib.dll" })
        {
            var path = Path.Combine(payload, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, "fixture", ct);
        }
        foreach (var file in Directory.EnumerateFiles(fixture, "ArkServerManager.ProcessFixture*")) File.Copy(file, Path.Combine(payload, Path.GetFileName(file)));
        File.Copy(Path.Combine(fixture, "ArkServerManager.ProcessFixture.exe"), Path.Combine(payload, "ArkServerManager.Desktop.exe"));
        var marker = Path.Combine(payload, "update-startup-fixture.txt"); await File.WriteAllTextAsync(marker, "ready", ct);
        async Task ManifestAsync()
        {
            var files = new List<PayloadFile>();
            foreach (var path in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != "payload-manifest.json"))
            {
                await using var input = File.OpenRead(path);
                files.Add(new(Path.GetRelativePath(payload, path).Replace('\\', '/'), input.Length, Convert.ToHexString(await SHA256.HashDataAsync(input, ct))));
            }
            await new AtomicJsonStore<InstallerPayload>(Path.Combine(payload, "payload-manifest.json")).SaveAsync(new(1, "win-x64", true, files), ct);
        }
        await ManifestAsync();
        var installed = Path.Combine(evidence, "installed"); await VerifiedPayloadInstaller.InstallAsync(payload, installed, "win-x64", ct);
        Directory.CreateDirectory(AppBootstrapLayout.DirectoryFor(installed));
        var protectedLauncher = AppBootstrapLayout.ExecutableFor(installed, "win-x64"); File.Copy(launcher, protectedLauncher);
        var launcherHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(protectedLauncher, ct)));
        await File.WriteAllTextAsync(marker, "changed", ct); await ManifestAsync();
        var record = await AppUpdatePreparation.PrepareAsync(installed, payload, evidence, "win-x64", (work, token) => work(token), ct);
        var resultPath = Path.Combine(evidence, "result.json"); await AppUpdateLaunchRecovery.PublishAsync(record, resultPath, ct);
        await new AtomicJsonStore<AppUpdateRecord>(Path.Combine(record.RecoveryDirectory, "update.json")).SaveAsync(record with { Phase = AppUpdatePhase.Applying }, ct);
        File.Copy(marker, Path.Combine(installed, "update-startup-fixture.txt"), true);
        var start = new ProcessStartInfo(protectedLauncher) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--application"); start.ArgumentList.Add(installed);
        using var process = Process.Start(start) ?? throw new IOException("The standalone launcher did not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(deadline.Token);
        if (process.ExitCode != 0) throw new IOException("The standalone launcher failed to recover the installed fixture.");
        while (!File.Exists(resultPath + ".opened")) await Task.Delay(100, deadline.Token);
        var recovered = await AppUpdateLaunchRecovery.ReadAsync(installed, ct);
        var outcome = await new AtomicJsonStore<AppUpdateLaunchOutcome>(resultPath).LoadAsync(ct);
        if (recovered?.Record.Phase != AppUpdatePhase.RolledBack || outcome?.Updated != false
            || await File.ReadAllTextAsync(Path.Combine(installed, "update-startup-fixture.txt"), ct) != "ready"
            || launcherHash != Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(protectedLauncher, ct))))
            throw new IOException("Launcher recovery evidence did not match the restored fixture.");
        var report = new { StandaloneLauncherExecuted = true, MixedFileStateRestored = true, FixtureDesktopOpened = true,
            LauncherUnchanged = true, RealDesktopInteractionVerified = false, LauncherSHA256 = launcherHash.ToLowerInvariant() };
        await File.WriteAllTextAsync(Path.Combine(evidence, "verification.json"), JsonSerializer.Serialize(report), ct);
        return report;
    }
}
