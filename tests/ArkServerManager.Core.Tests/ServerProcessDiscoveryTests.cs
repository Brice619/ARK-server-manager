using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerProcessDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-running-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DetectsMatchingNativeChildAndRevalidatesIdentityBeforeAndAfterExit()
    {
        var platform = OperatingSystem.IsWindows() ? "Win64" : "Linux";
        var executableName = OperatingSystem.IsWindows() ? "ShooterGameServer.exe" : "ShooterGameServer";
        var directory = Path.Combine(root, "ShooterGame", "Binaries", platform);
        Directory.CreateDirectory(directory);
        var fixtureName = "ArkServerManager.ProcessFixture" + (OperatingSystem.IsWindows() ? ".exe" : "");
        foreach (var source in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixture")))
        {
            var destinationName = Path.GetFileName(source) == fixtureName ? executableName : Path.GetFileName(source);
            File.Copy(source, Path.Combine(directory, destinationName));
        }
        var executable = Path.Combine(directory, executableName);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var child = ManagedProcess.Start(new(executable, directory, ["--wait"], Path.Combine(root, "fixture.log")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!child.ConsoleTail.Any(line => line.Text == "ready")) await Task.Delay(10, timeout.Token);
        var discovery = new ServerProcessDiscovery();
        var scan = await discovery.ScanAsync(timeout.Token);
        var found = Assert.Single(scan.Candidates, candidate => candidate.ProcessId == child.Id);
        Assert.Equal(Path.GetFullPath(root), found.Directory);
        Assert.Equal(executable, found.Executable);
        Assert.Equal(ArkEdition.ASE, found.Edition);
        Assert.True(discovery.StillMatches(found));
        Assert.Equal(ProcessIdentityState.Matches, discovery.CheckIdentity(found).State);
        Assert.False(discovery.StillMatches(found with { StartedAt = found.StartedAt.AddSeconds(-1) }));
        Assert.False(discovery.StillMatches(found with { Executable = executable + ".other" }));
        await child.SendStandardInputAsync("stop", timeout.Token);
        await child.Completion.WaitAsync(timeout.Token);
        Assert.False(discovery.StillMatches(found));
        Assert.Equal(ProcessIdentityState.NoLongerMatches, discovery.CheckIdentity(found).State);
        Assert.DoesNotContain((await discovery.ScanAsync(timeout.Token)).Candidates, candidate => candidate.ProcessId == child.Id);
    }

    [Fact]
    public async Task CancellationDoesNotReturnAnEmptySuccessfulScan()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ServerProcessDiscovery().ScanAsync(canceled.Token));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
