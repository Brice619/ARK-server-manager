using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class InstallationTests
{
    [Fact]
    public void InstallsDedicatedServerAppWithExplicitPlatformAndAnonymousLogin()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ARK Servers", "Island");
        var args = SteamCmdInstaller.Arguments(new(ArkEdition.ASA, ServerPlatform.Windows, directory, true));
        Assert.Contains("2430930", args);
        Assert.Contains("windows", args);
        Assert.Contains("anonymous", args);
        Assert.Contains("validate", args);
        Assert.Equal(directory, args[args.ToList().IndexOf("+force_install_dir") + 1]);
        Assert.Equal("+quit", args[^1]);
        Assert.Equal(376030u, SteamCmdInstaller.AppId(ArkEdition.ASE));
    }

    [Fact]
    public void SelectsWindowsAsaPayloadOnLinuxAndRejectsDangerousTargets()
    {
        Assert.Contains("windows", SteamCmdInstaller.Arguments(new(ArkEdition.ASA, ServerPlatform.Linux,
            Path.Combine(Path.GetTempPath(), "ASA Linux payload"), false)));
        Assert.Throws<ArgumentException>(() => SteamCmdInstaller.Arguments(new(ArkEdition.ASE, ServerPlatform.Windows, "server\n+quit", false)));
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;
        Assert.Throws<ArgumentException>(() => SteamCmdInstaller.Arguments(new(ArkEdition.ASE, ServerPlatform.Windows, root, false)));
    }
}
