using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class LinuxSteamCmdRuntimePlanTests
{
    [Theory]
    [InlineData("ID=ubuntu\n", "lib32gcc-s1")]
    [InlineData("ID=fedora\n", "glibc.i686")]
    [InlineData("ID=arch\n", "lib32-glibc")]
    public void UsesFixedDistributionPackageNames(string release, string package)
        => Assert.Contains(package, LinuxSteamCmdRuntimePlan.Select(release).Arguments);
    [Fact]
    public void Refuses64BitLibrariesAndAcceptsDistributionLoaderOutput()
    {
        var header = new byte[20]; header[0] = 0x7f; header[1] = (byte)'E'; header[2] = (byte)'L'; header[3] = (byte)'F';
        header[4] = header[5] = 1; header[18] = 3;
        Assert.True(LinuxSteamCmdRuntimePlan.IsElf32(header));
        header[4] = 2; Assert.False(LinuxSteamCmdRuntimePlan.IsElf32(header));
        Assert.True(LinuxSteamCmdRuntimePlan.SupportedLoaderVersion("ld.so (Ubuntu GLIBC 2.42) stable release"));
        Assert.True(LinuxSteamCmdRuntimePlan.SupportedLoaderVersion("ld.so (GNU libc) stable release"));
        Assert.False(LinuxSteamCmdRuntimePlan.SupportedLoaderVersion("unrelated executable"));
    }
}
