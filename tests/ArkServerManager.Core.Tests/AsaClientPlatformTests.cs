using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class AsaClientPlatformTests
{
    [Fact]
    public void EveryNonemptyCombinationUsesOnlySelectedPlatformTokens()
    {
        for (var selection = 1; selection <= 15; selection++)
        {
            var platforms = (AsaClientPlatforms)selection;
            var argument = AsaClientPlatformArguments.Build(platforms);
            if (platforms == AsaClientPlatforms.All) { Assert.Equal("-ServerPlatform=ALL", argument); continue; }
            var tokens = argument.Split('=')[1].Split('+');
            Assert.Equal(platforms.HasFlag(AsaClientPlatforms.PC), tokens.Contains("PC"));
            Assert.Equal(platforms.HasFlag(AsaClientPlatforms.WindowsStore), tokens.Contains("WINGDK"));
            Assert.Equal(platforms.HasFlag(AsaClientPlatforms.XboxSeries), tokens.Contains("XSX"));
            Assert.Equal(platforms.HasFlag(AsaClientPlatforms.PlayStation5), tokens.Contains("PS5"));
            Assert.Equal(tokens.Length, tokens.Distinct().Count());
        }
        foreach (var invalid in new[] { 0, 16, -1, 31 })
            Assert.Throws<ArgumentException>(() => AsaClientPlatformArguments.Build((AsaClientPlatforms)invalid));
    }
}
