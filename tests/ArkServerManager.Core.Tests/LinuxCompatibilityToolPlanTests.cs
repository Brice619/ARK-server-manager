using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class LinuxCompatibilityToolPlanTests
{
    [Theory]
    [InlineData("ID=ubuntu", "/usr/bin/apt-get", "python3")]
    [InlineData("ID=fedora", "/usr/bin/dnf", "python3")]
    [InlineData("ID=arch", "/usr/bin/pacman", "python")]
    public void FixedDistributionCommandsInstallPythonAndCertificates(string release, string executable, string package)
    {
        var plan = LinuxCompatibilityToolPlan.SelectPythonInstall(release);
        Assert.Equal(executable, plan.Executable); Assert.Contains(package, plan.Arguments); Assert.Contains("ca-certificates", plan.Arguments);
        Assert.DoesNotContain("btrfs-progs", plan.Arguments);
    }
    [Theory]
    [InlineData("Python 3.10.0", true)]
    [InlineData("Python 3.14.1\n", true)]
    [InlineData("Python 3.9.18", false)]
    [InlineData("not Python", false)]
    public void LauncherRequiresPython310OrNewer(string text, bool expected) => Assert.Equal(expected, LinuxCompatibilityToolPlan.SupportedPython(text));
}
