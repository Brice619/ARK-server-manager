using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class ClusterSettingOverrideTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturningToInheritanceChangesOnlyReleasedKeys(bool absentSharedValue)
    {
        var cluster = Guid.NewGuid();
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.GetTempPath(),
            new() { Name = "Map", MaxPlayers = 17, GamePort = 7777, QueryPort = 27015, RconPort = 32330 }, cluster) { Mods = ["111"] };
        var key = new ClusterSettingOverride("Game.ini", "MyMod", "Repeated");
        var expected = new ClusterOverrideReview(server.Id, cluster, server.Directory, [key], true, true);
        var edit = new ClusterOverrideSave(expected, [], false, false, true);
        var shared = new ServerPreset("Shared", ArkEdition.ASE, server.Settings with { MaxPlayers = 80 }, ["222"])
        { GameSettings = absentSharedValue ? "" : "[MyMod]\nRepeated=shared1\nRepeated=shared2\n", UserSettings = "[ServerSettings]\nXPMultiplier=99\n" };
        var user = "[ServerSettings]\nXPMultiplier=7\nServerAdminPassword=keep-private\n";
        var game = "[MyMod]\nRepeated=local1\nRepeated=local2\nOther=keep\n[OtherMod]\nRepeated=untouched\n";
        var reset = ClusterInheritance.BuildReset(server, edit, shared, user, game);
        var plan = PresetApplication.Build(server, reset.Preset, user, game, removals: reset.Removals);
        Assert.Equal(80, plan.Settings.MaxPlayers); Assert.Equal(["222"], plan.Mods);
        Assert.Equal(["7"], new IniDocument(plan.UserSettings).GetValues("ServerSettings", "XPMultiplier"));
        Assert.Equal(["keep-private"], new IniDocument(plan.UserSettings).GetValues("ServerSettings", "ServerAdminPassword"));
        var result = new IniDocument(plan.GameSettings);
        Assert.Equal(absentSharedValue ? Array.Empty<string>() : ["shared1", "shared2"], result.GetValues("MyMod", "Repeated"));
        Assert.Equal(["keep"], result.GetValues("MyMod", "Other")); Assert.Equal(["untouched"], result.GetValues("OtherMod", "Repeated"));
    }
    [Fact]
    public void ClusterApplicationKeepsLocalRepeatedSettingsCapacityAndMods()
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.GetTempPath(),
            new() { Name = "Map", MaxPlayers = 17, GamePort = 7777, QueryPort = 27015, RconPort = 32330 }, Guid.NewGuid())
        {
            Mods = ["111"], OverrideClusterMaxPlayers = true, OverrideClusterMods = true,
            ClusterOverrides = [new("GameUserSettings.ini", "ServerSettings", "HarvestAmountMultiplier"), new("Game.ini", "MyMod", "Mode")]
        };
        var preset = new ServerPreset("Shared", ArkEdition.ASE, server.Settings with { MaxPlayers = 80 }, ["222"])
        { UserSettings = "[ServerSettings]\nHarvestAmountMultiplier=9\nTamingSpeedMultiplier=8\n", GameSettings = "[MyMod]\nMode=shared\n" };
        var current = "[ServerSettings]\n; keep this comment\nHarvestAmountMultiplier=2\nTamingSpeedMultiplier=1\n";
        var result = PresetApplication.Build(server, preset, current, "[MyMod]\nMode=local1\nMode=local2\n", preserveClusterOverrides: true);
        Assert.Equal(17, result.Settings.MaxPlayers); Assert.Equal(["111"], result.Mods);
        var user = new IniDocument(result.UserSettings);
        Assert.Equal(["2"], user.GetValues("ServerSettings", "HarvestAmountMultiplier"));
        Assert.Equal(["8"], user.GetValues("ServerSettings", "TamingSpeedMultiplier"));
        Assert.Equal(["local1", "local2"], new IniDocument(result.GameSettings).GetValues("MyMod", "Mode"));
        Assert.Contains("; keep this comment", result.UserSettings);
        var ordinary = PresetApplication.Build(server, preset, current, "[MyMod]\nMode=local1\n");
        Assert.Equal(80, ordinary.Settings.MaxPlayers); Assert.Equal(["222"], ordinary.Mods);
    }
}
