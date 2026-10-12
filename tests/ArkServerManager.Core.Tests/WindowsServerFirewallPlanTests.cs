using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class WindowsServerFirewallPlanTests
{
    [Theory]
    [InlineData(ArkEdition.ASE)]
    [InlineData(ArkEdition.ASA)]
    public void PlayerRuleIsBoundToExecutableAndDoesNotExposeRcon(ArkEdition edition)
    {
        var server = new ServerDefinition(Guid.NewGuid(), edition, "TheIsland", Path.Combine(Path.GetTempPath(), "O'Brien $()` server"),
            new() { Name = "Test", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        var local = WindowsServerFirewallPlan.Build(server, ServerNetworkScope.LocalNetwork);
        Assert.Equal("LocalSubnet", local.RemoteAddress);
        Assert.Equal(new[] { 7777, 7778, 27015 }, local.Ports);
        Assert.DoesNotContain(server.Settings.RconPort, local.Ports);
        Assert.Equal("Any", WindowsServerFirewallPlan.Build(server, ServerNetworkScope.Internet).RemoteAddress);
        var script = WindowsServerFirewallPlan.CreateScript(server, ServerNetworkScope.LocalNetwork);
        Assert.Contains("O''Brien $()` server", script);
        Assert.Contains("-Program '", script);
        Assert.DoesNotContain("32330", script);
        Assert.Contains("Review it before replacing", script);
    }
}
