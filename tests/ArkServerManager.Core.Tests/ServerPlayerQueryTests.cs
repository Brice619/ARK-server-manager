using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerPlayerQueryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Server received, But no response!!")]
    [InlineData("unexpected response")]
    [InlineData("0. Player, account\n0. Other, different")]
    [InlineData("0. Player, account\n1. Other, account")]
    [InlineData("0. Player, bad id")]
    public void UnknownOrAmbiguousResponsesDoNotBecomeAnEmptyPlayerList(string response)
        => Assert.Throws<InvalidDataException>(() => ServerPlayerQuery.Parse(Guid.NewGuid(), response, DateTimeOffset.UtcNow));

    [Fact]
    public void PreservesNamesAndOpaqueAccountIdsWithoutGuessingPlatformOrTotalPopulation()
    {
        var result = ServerPlayerQuery.Parse(Guid.NewGuid(), "0. River, Stone, abc-def_123\r\n1. 玩家, 76561198000000000\n", DateTimeOffset.UtcNow);
        Assert.Equal("River, Stone", result.Players[0].Name);
        Assert.Equal("abc-def_123", result.Players[0].AccountId);
        Assert.Equal("玩家", result.Players[1].Name);
        Assert.False(result.CompleteListConfirmed);
        Assert.DoesNotContain("River", result.ToString());
    }

    [Fact]
    public void OnlyExplicitNoPlayersResponseConfirmsAnEmptyList()
    {
        var result = ServerPlayerQuery.Parse(Guid.NewGuid(), "No Players Connected\r\n", DateTimeOffset.UtcNow);
        Assert.Empty(result.Players);
        Assert.True(result.CompleteListConfirmed);
    }
}
