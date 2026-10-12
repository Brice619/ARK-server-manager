using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerInstallationResumeGuardTests
{
    [Theory]
    [InlineData("clear", true)]
    [InlineData("other-tool", true)]
    [InlineData("installer", false)]
    [InlineData("unresolved-installer", false)]
    [InlineData("server", false)]
    [InlineData("unresolved-server", false)]
    [InlineData("stale-installer", false)]
    [InlineData("stale-server", false)]
    public async Task NativeEvidenceMustExcludeInstallerAndStagedServer(string evidence, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-resume-guard-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "tool", OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        await File.WriteAllTextAsync(executable, "fixture tool");
        try
        {
            var record = new ServerInstallationRecord(1, Guid.NewGuid(), ArkEdition.ASE,
                OperatingSystem.IsWindows() ? ServerPlatform.Windows : ServerPlatform.Linux,
                Path.Combine(root, "stage"), Path.Combine(root, "final"), ServerInstallationPhase.Failed);
            var guard = new ServerInstallationResumeGuard(executable,
                _ => Task.FromResult(new SteamCmdProcessScan(evidence == "stale-installer" ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow,
                    evidence == "installer" ? [new(100, Path.Combine(root, "tool", "linux32", "steamcmd"))]
                        : evidence == "other-tool" ? [new(101, Path.Combine(root, "other", "steamcmd.exe"))] : [],
                    evidence == "unresolved-installer" ? 1 : 0)),
                _ => Task.FromResult(new ServerProcessScan(evidence == "stale-server" ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow,
                    evidence == "server" ? [new(200, DateTimeOffset.UtcNow, Path.Combine(record.StagingDirectory, "server.exe"),
                        record.StagingDirectory, ArkEdition.ASE, record.Platform.ToString())] : [],
                    evidence == "unresolved-server" ? [new(201, "ShooterGameServer", "unavailable")] : [], [])));
            Assert.Equal(expected, await guard.VerifyAsync(record));
            Assert.False(Directory.Exists(record.StagingDirectory));
            Assert.False(Directory.Exists(record.FinalDirectory));
        }
        finally { Directory.Delete(root, true); }
    }
}
