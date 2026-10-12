using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class NativeServerLaunchPlanTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-launch-plan-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ArkEdition.ASE, ServerPlatform.Windows, "ShooterGame/Binaries/Win64/ShooterGameServer.exe")]
    [InlineData(ArkEdition.ASE, ServerPlatform.Linux, "ShooterGame/Binaries/Linux/ShooterGameServer")]
    [InlineData(ArkEdition.ASA, ServerPlatform.Windows, "ShooterGame/Binaries/Win64/ArkAscendedServer.exe")]
    public void BuildsDirectEditionSpecificArguments(ArkEdition edition, ServerPlatform platform, string binary)
    {
        var directory = Path.Combine(root, "Server with spaces");
        var executable = Path.Combine(directory, binary);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "not executable");
        var clusterId = Guid.NewGuid();
        var server = new ServerDefinition(Guid.NewGuid(), edition,
            "TheIsland", directory,
            new() { Name = "Named server", GamePort = 7787, QueryPort = 27025, RconPort = 32340 }, clusterId)
            { Mods = ["123", "456"] };
        var shared = Path.Combine(root, "Shared cluster data");
        var plan = NativeServerLaunchPlan.Build(server, new(platform, false, shared), Path.Combine(root, "console.log"));
        Assert.Equal(WindowsNativePath.ExpandExisting(executable), WindowsNativePath.ExpandExisting(plan.Executable));
        Assert.Equal(Path.GetFileName(executable), Path.GetFileName(plan.Executable));
        Assert.Contains("-ClusterDirOverride=" + shared, plan.Arguments);
        Assert.Contains("-clusterid=" + clusterId.ToString("N"), plan.Arguments);
        Assert.DoesNotContain("-NoBattlEye", plan.Arguments);
        if (edition == ArkEdition.ASA)
        { Assert.Contains("TheIsland_WP?listen", plan.Arguments); Assert.Contains("-mods=123,456", plan.Arguments); Assert.Contains("-port=7787", plan.Arguments); }
        else Assert.Contains("TheIsland?listen?Port=7787?QueryPort=27025?MaxPlayers=70", plan.Arguments);
        Assert.False(Directory.Exists(shared));
        Assert.False(File.Exists(plan.LogFile));
    }

    [Fact]
    public void ManagedClusterMembersUseSameTransfersDirectoryOutsideBothServerFolders()
    {
        var cluster = Guid.NewGuid();
        var servers = new List<ServerDefinition>();
        for (var index = 0; index < 2; index++)
        {
            var directory = Path.Combine(root, "servers", "map-" + index);
            var binary = Path.Combine(directory, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!); File.WriteAllText(binary, "inert fixture");
            servers.Add(new(Guid.NewGuid(), ArkEdition.ASE, index == 0 ? "TheIsland" : "Ragnarok", directory,
                new() { Name = "Map " + index, GamePort = 7777 + 2 * index, QueryPort = 27015 + index, RconPort = 32330 + index }, cluster));
        }
        var catalog = new CatalogDocument(1, [new(cluster, "Cluster", ArkEdition.ASE, new())], servers);
        var storage = ManagedClusterStorage.Resolve(catalog, cluster, root);
        foreach (var server in servers)
        {
            var plan = NativeServerLaunchPlan.BuildManaged(catalog, server.Id, root, ServerPlatform.Windows, false, Path.Combine(root, "console.log"));
            Assert.Contains("-ClusterDirOverride=" + storage.Transfers, plan.Arguments);
            Assert.DoesNotContain("-ClusterDirOverride=" + storage.Backups, plan.Arguments);
            Assert.Contains("-clusterid=" + cluster.ToString("N"), plan.Arguments);
            var deletion = ServerDeletionPlan.Create(catalog, server.Id, root, storage.ProtectedPaths);
            Assert.Equal([storage.Directory], deletion.ProtectedSharedPaths);
        }
        Assert.False(Directory.Exists(storage.Directory));
    }

    [Theory]
    [InlineData(AseClientAccess.Steam, null)]
    [InlineData(AseClientAccess.SteamAndEpic, "-crossplay")]
    [InlineData(AseClientAccess.Epic, "-epiconly")]
    public void AseClientSelectionUsesOnlyItsCorrespondingFlag(AseClientAccess access, string? flag)
    {
        var binary = Path.Combine(root, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!); File.WriteAllText(binary, "fixture");
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root,
            new() { Name = "Test", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }) { AseClients = access };
        var plan = NativeServerLaunchPlan.Build(server, new(ServerPlatform.Windows, false, null), Path.Combine(root, "console.log"));
        Assert.Equal(flag == "-crossplay", plan.Arguments.Contains("-crossplay"));
        Assert.Equal(flag == "-epiconly", plan.Arguments.Contains("-epiconly"));
        if (access != AseClientAccess.Steam)
            Assert.Throws<InvalidOperationException>(() => NativeServerLaunchPlan.Build(server with { Mods = ["2182894352"] },
                new(ServerPlatform.Windows, false, null), Path.Combine(root, "console.log")));
    }

    [Fact]
    public void AsaLinuxRequiresCompatibilityAdapter()
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASA, "TheIsland_WP", root,
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        Assert.Throws<NotSupportedException>(() => NativeServerLaunchPlan.Build(server,
            new(ServerPlatform.Linux, false, null), Path.Combine(root, "console.log")));
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("member")]
    [InlineData("child")]
    [InlineData("file")]
    public void RejectsOverlappingOrNonDirectorySharedData(string location)
    {
        var directory = Path.Combine(root, "member");
        var executable = Path.Combine(directory, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "not executable");
        var shared = location switch
        {
            "parent" => root,
            "member" => directory,
            "child" => Path.Combine(directory, "shared"),
            _ => Path.Combine(root, "shared-file")
        };
        if (location == "file") File.WriteAllText(shared, "unchanged");
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", directory,
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }, Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => NativeServerLaunchPlan.Build(server,
            new(ServerPlatform.Windows, false, shared), Path.Combine(root, "console.log")));
        Assert.False(File.Exists(Path.Combine(root, "console.log")));
        if (location == "file") Assert.Equal("unchanged", File.ReadAllText(shared));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
