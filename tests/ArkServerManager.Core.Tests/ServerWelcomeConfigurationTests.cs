using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerWelcomeConfigurationTests
{
    [Fact]
    public void ReplacesDuplicatesPreservesUnrelatedSettingsAndCanReturnToDefaultDuration()
    {
        var source = "; keep this comment\r\n[ServerSettings]\r\nServerAdminPassword=private\r\nUnknownModOption=42\r\n"
            + "[MessageOfTheDay]\r\nMessage=old\r\nDuration=10\r\nDuration=30\r\nColor=blue\r\n";
        var changed = ServerWelcomeConfiguration.Apply(source, "New welcome", 45);
        var ini = new IniDocument(changed);
        Assert.Equal(["New welcome"], ini.GetValues("MessageOfTheDay", "Message"));
        Assert.Equal(["45"], ini.GetValues("MessageOfTheDay", "Duration"));
        Assert.Equal(["blue"], ini.GetValues("MessageOfTheDay", "Color"));
        Assert.Contains("; keep this comment\r\n", changed);
        Assert.Contains("UnknownModOption=42\r\n", changed);
        Assert.Contains("ServerAdminPassword=private\r\n", changed);
        var cleared = new IniDocument(ServerWelcomeConfiguration.Apply(changed, "", null));
        Assert.Equal([""], cleared.GetValues("MessageOfTheDay", "Message"));
        Assert.Empty(cleared.GetValues("MessageOfTheDay", "Duration"));
    }
}
