using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerRconConfigurationTests
{
    private static ServerDefinition Server => new(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", "fixture",
        new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });

    [Fact]
    public void ResolvesActualPasswordAndRedactsDiagnosticString()
    {
        var endpoint = ServerRconConfiguration.FromConfiguration(Server,
            "[ServerSettings]\nRCONEnabled=True\nRCONPort=32330\nServerAdminPassword=secret-value\n",
            TimeSpan.FromSeconds(5), RconResponseMode.SinglePacket);
        Assert.Equal(32330, endpoint.Port);
        Assert.Equal("secret-value", endpoint.Password);
        Assert.DoesNotContain("secret-value", endpoint.ToString());
    }

    [Theory]
    [InlineData("RCONEnabled=False\nRCONPort=32330\nServerAdminPassword=secret")]
    [InlineData("RCONEnabled=True\nRCONPort=32331\nServerAdminPassword=secret")]
    [InlineData("RCONEnabled=True\nRCONPort=32330\nServerAdminPassword=")]
    [InlineData("RCONEnabled=True\nRCONPort=32330\nServerAdminPassword=one\nServerAdminPassword=two")]
    public void RejectsUnusableOrAmbiguousConnection(string settings)
        => Assert.Throws<InvalidDataException>(() => ServerRconConfiguration.FromConfiguration(Server,
            "[ServerSettings]\n" + settings, TimeSpan.FromSeconds(5), RconResponseMode.SinglePacket));
}
