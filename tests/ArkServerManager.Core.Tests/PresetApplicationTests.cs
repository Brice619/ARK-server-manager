using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class PresetApplicationTests
{
    [Fact]
    public void ExistingServerPresetPlanPreservesIdentitySecretsAndUnrelatedSettings()
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.GetTempPath(),
            new() { Name = "Current server", MaxPlayers = 20, GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        var preset = new ServerPreset("Rates", ArkEdition.ASE, new() { MaxPlayers = 40 }, ["123", "456"])
        {
            UserSettings = "[ServerSettings]\nServerPassword=replace-me\nServerAdminPassword=replace-admin\nTamingSpeedMultiplier=5\n[SessionSettings]\nSessionName=Wrong name\nPort=1234\n[/Script/ShooterGame.ShooterGameUserSettings]\nVersion=999\n",
            GameSettings = "[/Script/ShooterGame.ShooterGameMode]\nCustomEntry=one\nCustomEntry=two\n"
        };
        server.Settings.Values["TamingSpeedMultiplier"] = "1";
        var plan = PresetApplication.Build(server, preset,
            "; keep comment\n[ServerSettings]\nServerPassword=current-join\nServerAdminPassword=current-admin\nRCONEnabled=True\nRCONPort=32330\nKeepMe=yes\n[/Script/ShooterGame.ShooterGameUserSettings]\nVersion=5\n",
            "[Unknown]\nKeep=original\n");
        var user = new IniDocument(plan.UserSettings); var game = new IniDocument(plan.GameSettings);
        Assert.Equal(["current-join"], user.GetValues("ServerSettings", "ServerPassword"));
        Assert.Equal(["current-admin"], user.GetValues("ServerSettings", "ServerAdminPassword"));
        Assert.Equal(["Current server"], user.GetValues("SessionSettings", "SessionName"));
        Assert.Equal(["7777"], user.GetValues("SessionSettings", "Port"));
        Assert.Equal(["5"], user.GetValues("/Script/ShooterGame.ShooterGameUserSettings", "Version"));
        Assert.Equal(["yes"], user.GetValues("ServerSettings", "KeepMe"));
        Assert.Contains("; keep comment", plan.UserSettings);
        Assert.Equal(["5"], user.GetValues("ServerSettings", "TamingSpeedMultiplier"));
        Assert.Equal(["one", "two"], game.GetValues("/Script/ShooterGame.ShooterGameMode", "CustomEntry"));
        Assert.Equal(["original"], game.GetValues("Unknown", "Keep"));
        Assert.Equal(["123", "456"], game.GetValues("ModInstaller", "ModIDS"));
        Assert.Equal(["123,456"], user.GetValues("ServerSettings", "ActiveMods"));
        Assert.Equal(40, plan.Settings.MaxPlayers);
        Assert.Equal("5", plan.Settings.Values["TamingSpeedMultiplier"]);
        Assert.Equal("1", server.Settings.Values["TamingSpeedMultiplier"]);
        Assert.Equal(server.Settings.GamePort, plan.Settings.GamePort);
        Assert.DoesNotContain("current-admin", plan.ToString());
        Assert.Equal(20, server.Settings.MaxPlayers);
    }

    [Fact]
    public void DifferentGamePresetIsRefused()
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.GetTempPath(),
            new() { Name = "Current", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        Assert.Throws<ArgumentException>(() => PresetApplication.Build(server, new("Other game", ArkEdition.ASA, new(), []), "", ""));
    }
}
