using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class LinuxAsaLaunchPlanTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-umu-plan-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void AsaIsPassedToPythonZipappWithTokenizedArgumentsAndDedicatedEnvironment()
    {
        var directory = Path.Combine(root, "server with spaces");
        var binary = Path.Combine(directory, "ShooterGame", "Binaries", "Win64", "ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!); File.WriteAllText(binary, "inert payload");
        var python = Path.Combine(root, "python3"); var umu = Path.Combine(root, "umu-run");
        File.WriteAllText(python, "inert Python"); File.WriteAllText(umu, "inert zipapp");
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASA, "TheIsland_WP", directory,
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }) { Mods = ["928708"] };
        var plan = LinuxAsaLaunchPlan.BuildManaged(new(1, [], [server]), server.Id, root, python, umu, true, Path.Combine(root, "console.log"));
        Assert.Equal(python, plan.Executable); Assert.Equal(Path.GetDirectoryName(binary), plan.WorkingDirectory);
        Assert.Equal(umu, plan.Arguments[0]); Assert.Equal(binary, plan.Arguments[1]);
        Assert.Contains("TheIsland_WP?listen", plan.Arguments); Assert.Contains("-mods=928708", plan.Arguments);
        Assert.Contains("-NoBattlEye", plan.Arguments); Assert.Contains("-port=7777", plan.Arguments);
        Assert.Equal(UmuServerEnvironment.Build(root, server.Id), plan.EnvironmentOverrides);
        Assert.False(File.Exists(plan.LogFile));
    }
    [Theory]
    [InlineData("/home/ark/Shared cluster", "Z:\\home\\ark\\Shared cluster")]
    [InlineData("/srv/ark/Clusters/id/Transfers", "Z:\\srv\\ark\\Clusters\\id\\Transfers")]
    public void ClusterDirectoryIsMappedToWindowsArgumentWithoutShellQuoting(string path, string expected) =>
        Assert.Equal(expected, LinuxAsaLaunchPlan.UnixPathForWine(path));
    [Theory]
    [InlineData("relative")]
    [InlineData("//host/share")]
    [InlineData("/srv/../elsewhere")]
    [InlineData("/srv/./ark")]
    [InlineData("/srv/ark:other")]
    [InlineData("/srv/ark\\other")]
    public void AmbiguousClusterPathsAreRefused(string path) =>
        Assert.Throws<ArgumentException>(() => LinuxAsaLaunchPlan.UnixPathForWine(path));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
