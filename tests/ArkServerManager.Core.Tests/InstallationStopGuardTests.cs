using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class InstallationStopGuardTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-stop-guard-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Executable => Path.Combine(Source, "ShooterGame", "Binaries", OperatingSystem.IsWindows() ? "Win64" : "Linux",
        OperatingSystem.IsWindows() ? "ShooterGameServer.exe" : "ShooterGameServer");
    private void Seed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Executable)!);
        File.WriteAllText(Executable, "fixture");
    }
    private ServerProcessCandidate Running() => new(100, DateTimeOffset.UtcNow, Executable, Source, ArkEdition.ASE,
        OperatingSystem.IsWindows() ? "Windows" : "Linux");
    private static ServerProcessScan Scan(params ServerProcessCandidate[] processes) => new(DateTimeOffset.UtcNow, processes, [], []);

    [Fact]
    public async Task MissingInstallationIsUnverifiedWithoutScanningOrRequestingShutdown()
    {
        var guard = new InstallationStopGuard(_ => throw new InvalidOperationException("Missing paths must not scan processes"));
        var result = await guard.EnsureStoppedAsync(Source, (_, _) => throw new InvalidOperationException("No installation to stop"));
        Assert.Equal(InstallationStopState.Unverified, result.State);
        Assert.Contains(result.Findings, finding => finding.Contains("missing or inaccessible", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Findings, finding => finding.Contains("linked", StringComparison.Ordinal));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ShutdownApprovalIsFollowedByFreshEvidenceAndOtherInstallationDoesNotBlock()
    {
        Seed();
        var running = true;
        var scans = 0;
        var other = Running() with { ProcessId = 200, Directory = Path.Combine(root, "other") };
        var guard = new InstallationStopGuard(_ =>
        { scans++; return Task.FromResult(running ? Scan(Running(), other) : Scan(other)); });
        var result = await guard.EnsureStoppedAsync(Source, (check, _) =>
        {
            Assert.Single(check.Processes);
            Assert.Equal(100, check.Processes[0].ProcessId);
            running = false;
            return Task.FromResult(true);
        });
        Assert.Equal(InstallationStopState.Stopped, result.State);
        Assert.Equal(2, scans);
    }

    [Fact]
    public async Task ApprovalWithoutActualExitAndDecliningShutdownPreventTransfer()
    {
        Seed();
        var guard = new InstallationStopGuard(_ => Task.FromResult(Scan(Running())));
        var approved = await guard.EnsureStoppedAsync(Source, (_, _) => Task.FromResult(true));
        Assert.Equal(InstallationStopState.Running, approved.State);
        var declined = await guard.EnsureStoppedAsync(Source, (_, _) => Task.FromResult(false));
        Assert.Equal(InstallationStopState.Running, declined.State);
        var target = Path.Combine(root, "target");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new InstallationTransfer(guard, (_, _) => Task.FromResult(true))
            .ExecuteAsync(Source, target, ArkEdition.ASE, InstallationImportMode.Copy, true));
        Assert.True(File.Exists(Executable));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task UnresolvedEvidenceNeverBecomesStoppedAndDoesNotRequestBlindShutdown()
    {
        Seed();
        var guard = new InstallationStopGuard(_ => Task.FromResult(new ServerProcessScan(DateTimeOffset.UtcNow, [],
            [new(100, "ShooterGameServer", "Access denied")], [])));
        var result = await guard.EnsureStoppedAsync(Source, (_, _) => throw new InvalidOperationException("Must not stop unknown processes"));
        Assert.Equal(InstallationStopState.Unverified, result.State);
        Assert.NotEmpty(result.Findings);
        Assert.Contains(result.Findings, finding => finding.Contains("Process 100 (ShooterGameServer): Access denied", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new InstallationTransfer(guard, (_, _) => Task.FromResult(true))
            .ExecuteAsync(Source, Path.Combine(root, "target"), ArkEdition.ASE, InstallationImportMode.Move, true));
        Assert.True(File.Exists(Executable));
    }

    [Fact]
    public async Task AlreadyStoppedInstallationDoesNotAskAndIsCopiedWithFreshChecks()
    {
        Seed();
        var scans = 0;
        var guard = new InstallationStopGuard(_ => { scans++; return Task.FromResult(Scan()); });
        var target = Path.Combine(root, "target");
        var result = await new InstallationTransfer(guard, (_, _) => throw new InvalidOperationException("No shutdown needed"))
            .ExecuteAsync(Source, target, ArkEdition.ASE, InstallationImportMode.Copy, true);
        Assert.Equal(target, result.Directory);
        Assert.Equal(2, scans);
        Assert.True(File.Exists(Executable));
    }

    [Fact]
    public async Task LinkedDestinationCannotRedirectTransferOutsideSelectedFolder()
    {
        Seed();
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var linked = Path.Combine(root, "linked");
        if (OperatingSystem.IsWindows())
        {
            // Directory junctions exercise the same reparse-point guard without requiring symlink privileges.
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{linked}\" \"{outside}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var process = System.Diagnostics.Process.Start(start)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(linked, outside);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new InstallationTransfer((_, _) => Task.FromResult(true))
                .ExecuteAsync(Source, Path.Combine(linked, "target"), ArkEdition.ASE, InstallationImportMode.Copy, true));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
            Assert.True(File.Exists(Executable));
        }
        finally { Directory.Delete(linked); }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
