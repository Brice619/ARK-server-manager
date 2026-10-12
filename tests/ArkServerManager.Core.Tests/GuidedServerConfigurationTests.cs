using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class GuidedServerConfigurationTests
{
    private static ServerDefinition Server() => new(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.GetTempPath(),
        new() { Name = "Chosen world", MaxPlayers = 30, GamePort = 7777, QueryPort = 27015, RconPort = 32330 });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConvertsExplicitChoicesWithoutAddingGameplayDefaults(bool pve)
    {
        var server = Server();
        var request = new GuidedServerConfigurationRequest { Server = server, PvE = pve, AdministratorPassword = "private-admin", JoinPassword = "private-join", WelcomeMessage = "Welcome to our ARK!" };
        var configured = GuidedServerConfiguration.Build(request); var ini = new IniDocument(configured.UserSettings);
        Assert.Equal(server.Id, configured.ServerId);
        Assert.Equal(["5"], ini.GetValues("/Script/ShooterGame.ShooterGameUserSettings", "Version"));
        Assert.Equal(["Chosen world"], ini.GetValues("SessionSettings", "SessionName"));
        Assert.Equal(["30"], ini.GetValues("/Script/Engine.GameSession", "MaxPlayers"));
        Assert.Equal(["7777"], ini.GetValues("SessionSettings", "Port"));
        Assert.Equal(["27015"], ini.GetValues("SessionSettings", "QueryPort"));
        Assert.Equal([pve ? "True" : "False"], ini.GetValues("ServerSettings", "ServerPVE"));
        Assert.Equal(["private-join"], ini.GetValues("ServerSettings", "ServerPassword"));
        Assert.Equal(["Welcome to our ARK!"], ini.GetValues("MessageOfTheDay", "Message"));
        Assert.Empty(ini.GetValues("MessageOfTheDay", "Duration"));
        ServerRconConfiguration.FromConfiguration(server, configured.UserSettings, TimeSpan.FromSeconds(5), RconResponseMode.SinglePacket);
        Assert.Empty(configured.GameSettings);
        Assert.DoesNotContain("private-admin", request.ToString()); Assert.DoesNotContain("private-admin", configured.ToString());
        Assert.Empty(ini.GetValues("ServerSettings", "TamingSpeedMultiplier"));
    }

    [Fact]
    public void WritesChosenWelcomeDurationAndRejectsInvalidDuration()
    {
        var request = new GuidedServerConfigurationRequest { Server = Server(), PvE = true,
            AdministratorPassword = "private", WelcomeMessage = "Welcome", WelcomeDurationSeconds = 45 };
        var ini = new IniDocument(GuidedServerConfiguration.Build(request).UserSettings);
        Assert.Equal(["45"], ini.GetValues("MessageOfTheDay", "Duration"));
        Assert.Equal(["Welcome"], ini.GetValues("MessageOfTheDay", "Message"));
        foreach (var invalid in new[] { 0, -1 })
            Assert.Throws<ArgumentException>(() => GuidedServerConfiguration.Build(new() { Server = Server(), PvE = true,
                AdministratorPassword = "private", WelcomeDurationSeconds = invalid }));
    }

    [Fact]
    public void TemplatesPreserveRepeatedGameplayValuesWhileApplyingReviewedIdentityAndAccess()
    {
        const string game = "[/Script/ShooterGame.ShooterGameMode]\nCustomEntry=one\nCustomEntry=two\n";
        var request = new GuidedServerConfigurationRequest { Server = Server(), PvE = true, AdministratorPassword = "new-admin",
            UserSettingsTemplate = "[SessionSettings]\nSessionName=Old\nPort=8000\n[ServerSettings]\nServerAdminPassword=old-admin\n[ModSettings]\nValue=first\nValue=second\n[MessageOfTheDay]\nMessage=Old welcome\nDuration=99\n",
            GameSettingsTemplate = game };
        var result = GuidedServerConfiguration.Build(request);
        var ini = new IniDocument(result.UserSettings);
        Assert.Equal(new[] { "first", "second" }, ini.GetValues("ModSettings", "Value"));
        Assert.Equal(new[] { "7777" }, ini.GetValues("SessionSettings", "Port"));
        Assert.Equal(new[] { "new-admin" }, ini.GetValues("ServerSettings", "ServerAdminPassword"));
        Assert.Empty(ini.GetValues("MessageOfTheDay", "Message")); Assert.Empty(ini.GetValues("MessageOfTheDay", "Duration"));
        Assert.Equal(game, result.GameSettings);
        GuidedServerConfiguration.ValidateIdentity(request.Server!, result.UserSettings);
    }

    [Fact]
    public void RequiresExplicitModeAndDoesNotSilentlyDiscardPresetOrModChoices()
    {
        var server = Server();
        Assert.Throws<ArgumentException>(() => GuidedServerConfiguration.Build(new() { Server = server, AdministratorPassword = "private" }));
        var result = GuidedServerConfiguration.Build(new() { Server = server with { Mods = ["123", "456"], Settings = server.Settings with
            { Values = new() { ["TamingSpeedMultiplier"] = "3", ["Game.ini::ModSettings::Value"] = "chosen" } } }, PvE = true, AdministratorPassword = "private" });
        Assert.Equal(new[] { "123,456" }, new IniDocument(result.UserSettings).GetValues("ServerSettings", "ActiveMods"));
        Assert.Equal(new[] { "123", "456" }, new IniDocument(result.GameSettings).GetValues("ModInstaller", "ModIDS"));
        Assert.Equal(new[] { "3" }, new IniDocument(result.UserSettings).GetValues("ServerSettings", "TamingSpeedMultiplier"));
        Assert.Equal(new[] { "chosen" }, new IniDocument(result.GameSettings).GetValues("ModSettings", "Value"));
        Assert.Throws<ArgumentException>(() => GuidedServerConfiguration.Build(new() { Server = server with { Mods = ["123", "0123"] }, PvE = true, AdministratorPassword = "private" }));
    }
}
