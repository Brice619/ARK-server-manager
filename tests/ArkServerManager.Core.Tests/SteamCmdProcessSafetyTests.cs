using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class SteamCmdProcessSafetyTests
{
    [Fact]
    public void OrphanedToolAndLinuxChildPreventAnotherOperation()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-steamcmd-safety"); var now = DateTimeOffset.UtcNow;
        foreach (var process in new[] { Path.Combine(root, "steamcmd.exe"), Path.Combine(root, "linux32", "steamcmd") })
            Assert.Throws<IOException>(() => SteamCmdProcessSafety.RequireIdle(Path.Combine(root, "steamcmd.sh"), new(now, [new(123, process)], 0), now, now));
        SteamCmdProcessSafety.RequireIdle(Path.Combine(root, "steamcmd.sh"), new(now, [new(456, Path.Combine(root + "-other", "steamcmd.exe"))], 0), now, now);
    }
    [Fact]
    public void UnresolvedOrStaleObservationCannotAuthorizeRepair()
    {
        var executable = Path.Combine(Path.GetTempPath(), "ark-steamcmd-safety", "steamcmd.exe"); var now = DateTimeOffset.UtcNow;
        Assert.Throws<IOException>(() => SteamCmdProcessSafety.RequireIdle(executable, new(now, [], 1), now, now));
        Assert.Throws<IOException>(() => SteamCmdProcessSafety.RequireIdle(executable, new(now.AddSeconds(-1), [], 0), now, now));
        SteamCmdProcessSafety.RequireIdle(executable, new(now, [], 0), now, now);
    }
}
