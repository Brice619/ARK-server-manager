using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerAccessConfigurationTests
{
    [Fact]
    public void ReplacesDuplicatedAccessSettingsAndPreservesUnrelatedTemplateContent()
    {
        const string original = "; keep comment\n[ServerSettings]\nServerAdminPassword=old\nRCONPort=1\nRCONEnabled=False\nServerPassword=oldJoin\nCustomSetting=keep\n[serversettings]\nserveradminpassword=duplicate\n";
        var text = ServerAccessConfiguration.Apply(original, 32330, "new-admin", "");
        var ini = new IniDocument(text);
        Assert.Equal(["new-admin"], ini.GetValues("ServerSettings", "ServerAdminPassword"));
        Assert.Equal([""], ini.GetValues("ServerSettings", "ServerPassword"));
        Assert.Contains("; keep comment", text); Assert.Contains("CustomSetting=keep", text);
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.GetTempPath(), new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        var endpoint = ServerRconConfiguration.FromConfiguration(server, text, TimeSpan.FromSeconds(5), RconResponseMode.SinglePacket);
        Assert.DoesNotContain("new-admin", endpoint.ToString());
    }

    [Theory]
    [InlineData("secret\nRCONEnabled=False")]
    [InlineData("secret\0")]
    [InlineData(" secret")]
    [InlineData("")]
    public void RejectsUnusableAdministratorValuesWithoutPrintingSecrets(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => ServerAccessConfiguration.Apply("", 32330, value, ""));
        if (value.Length > 0) Assert.DoesNotContain(value, error.Message);
    }
}
