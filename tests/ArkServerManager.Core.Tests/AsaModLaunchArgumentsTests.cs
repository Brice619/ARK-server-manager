using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class AsaModLaunchArgumentsTests
{
    [Fact]
    public void OrderBecomesOneArgumentWithoutChangingInputAndEmptyListOmitsArgument()
    {
        var input = new List<string> { "900062", "926919" };
        Assert.Equal("-mods=900062,926919", Assert.Single(AsaModLaunchArguments.Build(input)));
        Assert.Equal(new[] { "900062", "926919" }, input);
        Assert.Empty(AsaModLaunchArguments.Build([]));
        Assert.Throws<ArgumentException>(() => AsaModLaunchArguments.Build(["1", "01"]));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("123-dev")]
    [InlineData("123,456")]
    [InlineData("123 -other")]
    [InlineData("")]
    [InlineData("１２３")]
    public void RejectsInvalidPublicProjectIds(string value)
        => Assert.Throws<ArgumentException>(() => AsaModLaunchArguments.Build([value]));
}
