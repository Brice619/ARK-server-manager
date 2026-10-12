using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class BasicServerConfigurationTests
{
    [Fact]
    public void UpdatesBasicFieldsWithoutReplacingCredentialsWelcomeModsOrUnknownConfiguration()
    {
        const string source = "; preserve\r\n[ServerSettings]\r\nServerAdminPassword=private\r\nServerPassword=join\r\nRCONEnabled=True\r\nRCONPort=32330\r\n[MessageOfTheDay]\r\nMessage=welcome\r\nDuration=45\r\n[Mod]\r\nArray=one\r\nArray=two\r\n";
        var settings = new ServerSettings { Name = "Changed", MaxPlayers = 80, GamePort = 7787, QueryPort = 27025, RconPort = 32340 };
        var text = BasicServerConfiguration.Apply(source, settings);
        var ini = new IniDocument(text);
        Assert.Contains("; preserve\r\n", text);
        Assert.Equal(["Changed"], ini.GetValues("SessionSettings", "SessionName"));
        Assert.Equal(["7787"], ini.GetValues("SessionSettings", "Port"));
        Assert.Equal(["27025"], ini.GetValues("SessionSettings", "QueryPort"));
        Assert.Equal(["80"], ini.GetValues("/Script/Engine.GameSession", "MaxPlayers"));
        Assert.Equal(["32340"], ini.GetValues("ServerSettings", "RCONPort"));
        Assert.Equal(["private"], ini.GetValues("ServerSettings", "ServerAdminPassword"));
        Assert.Equal(["join"], ini.GetValues("ServerSettings", "ServerPassword"));
        Assert.Equal(["45"], ini.GetValues("MessageOfTheDay", "Duration"));
        Assert.Equal(["one", "two"], ini.GetValues("Mod", "Array"));
    }

    [Fact]
    public void RejectsNameInjectionBeforeProducingConfiguration()
        => Assert.Throws<ArgumentException>(() => BasicServerConfiguration.Apply("", new()
            { Name = "Name\nInjected=True", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }));
}
