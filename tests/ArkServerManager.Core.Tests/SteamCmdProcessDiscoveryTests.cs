using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class SteamCmdProcessDiscoveryTests
{
    [Fact]
    public async Task ActualInstallerNamedChildBlocksResumeUntilItExits()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-steamcmd-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixtureName = "ArkServerManager.ProcessFixture" + (OperatingSystem.IsWindows() ? ".exe" : "");
            var toolName = OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd";
            foreach (var source in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixture")))
                File.Copy(source, Path.Combine(root, Path.GetFileName(source) == fixtureName ? toolName : Path.GetFileName(source)));
            var executable = Path.Combine(root, toolName);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var child = ManagedProcess.Start(new(executable, root, ["--wait"], Path.Combine(root, "fixture.log")));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!child.ConsoleTail.Any(line => line.Text == "ready")) await Task.Delay(10, deadline.Token);
            var found = Assert.Single((await new SteamCmdProcessDiscovery().ScanAsync(deadline.Token)).Processes, p => p.ProcessId == child.Id);
            Assert.Equal(executable, found.Executable);
            var record = new ServerInstallationRecord(1, Guid.NewGuid(), ArkEdition.ASE,
                OperatingSystem.IsWindows() ? ServerPlatform.Windows : ServerPlatform.Linux,
                Path.Combine(root, "stage"), Path.Combine(root, "final"), ServerInstallationPhase.Failed);
            var guard = new ServerInstallationResumeGuard(executable);
            Assert.False(await guard.VerifyAsync(record, deadline.Token));
            await child.SendStandardInputAsync("stop", deadline.Token);
            await child.Completion.WaitAsync(deadline.Token);
            Assert.DoesNotContain((await new SteamCmdProcessDiscovery().ScanAsync(deadline.Token)).Processes, p => p.ProcessId == child.Id);
            Assert.True(await guard.VerifyAsync(record, deadline.Token));
        }
        finally { Directory.Delete(root, true); }
    }
}
