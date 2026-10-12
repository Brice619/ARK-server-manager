using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class RemappedLaunchDriveIdentityTests
{
    [Fact]
    public async Task ReplacingDriveTargetCannotHideTheOriginallyLoadedServerProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ark-remapped-identity-" + Guid.NewGuid().ToString("N"));
        var original = Path.Combine(root, "original"); var replacement = Path.Combine(root, "replacement");
        var relativeBinary = Path.Combine("ShooterGame", "Binaries", "Win64", "ArkAscendedServer.exe");
        var binary = Path.Combine(original, relativeBinary);
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixture")))
            File.Copy(file, Path.Combine(Path.GetDirectoryName(binary)!, Path.GetFileName(file) == "ArkServerManager.ProcessFixture.exe"
                ? "ArkAscendedServer.exe" : Path.GetFileName(file)));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(replacement, relativeBinary))!);
        await File.WriteAllTextAsync(Path.Combine(replacement, relativeBinary), "never launched");
        var backend = new NativeWindowsLaunchDriveBackend();
        var manager = new WindowsLaunchDrives(Path.Combine(root, "drives.json"), backend, (_, _) => Task.FromResult(true));
        var alias = await manager.GetOrCreateAsync(original);
        var currentTarget = "\\??\\" + original;
        ManagedProcess? process = null;
        try
        {
            var launchBinary = Path.Combine(alias, relativeBinary);
            process = ManagedProcess.Start(new(launchBinary, Path.GetDirectoryName(launchBinary)!, ["--wait"], Path.Combine(root, "console.log")));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!process.ConsoleTail.Any(line => line.Text == "ready")) await Task.Delay(20, deadline.Token);
            backend.RemoveExact(alias[0], currentTarget);
            currentTarget = "\\??\\" + replacement;
            backend.Create(alias[0], currentTarget);
            var scan = await new ServerProcessDiscovery().ScanAsync(deadline.Token);
            var observed = Assert.Single(scan.Candidates, candidate => candidate.ProcessId == process.Id);
            Assert.Equal(original, observed.Directory);
            Assert.Equal(InstallationStopState.Running, (await new InstallationStopGuard().CheckAsync(original, deadline.Token)).State);
            Assert.Equal(InstallationStopState.Stopped, (await new InstallationStopGuard().CheckAsync(replacement, deadline.Token)).State);
        }
        finally
        {
            if (process is not null) await process.DisposeAsync();
            // Both mapping targets in this fixture were created here. Preserve any
            // unexpected external replacement instead of deleting it.
            var targets = backend.Query(alias[0]);
            if (targets.Count == 1 && targets[0] == currentTarget) backend.RemoveExact(alias[0], currentTarget);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
