using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class LinuxBackupToolPlanTests
{
    [Theory]
    [InlineData("ID=ubuntu\nID_LIKE=debian", "/usr/bin/apt-get")]
    [InlineData("ID=pop\nID_LIKE=\"ubuntu debian\"", "/usr/bin/apt-get")]
    [InlineData("ID=fedora", "/usr/bin/dnf")]
    [InlineData("ID=manjaro\nID_LIKE=arch", "/usr/bin/pacman")]
    public void UsesOnlyFixedDistributionPackageCommands(string release, string executable)
    {
        var plan = LinuxBackupToolPlan.Select(release);
        Assert.Equal(executable, plan.Executable); Assert.Contains("btrfs-progs", plan.Arguments);
        Assert.DoesNotContain(plan.Arguments, value => value.Contains("upgrade", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("ID=\"ubuntu;touch /tmp/file\"")]
    [InlineData("ID=$(command)")]
    [InlineData("ID=ubuntu\nID=fedora")]
    public void UntrustedDistributionValuesCannotBecomeExecutableInstructions(string release)
        => Assert.Throws<InvalidDataException>(() => LinuxBackupToolPlan.Select(release));

    [Fact]
    public void UnknownDistributionsDoNotGuessAnInstaller()
        => Assert.Throws<NotSupportedException>(() => LinuxBackupToolPlan.Select("ID=unknown"));
}
