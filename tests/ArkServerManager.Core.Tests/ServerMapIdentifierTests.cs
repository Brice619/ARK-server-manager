using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerMapIdentifierTests
{
    [Theory]
    [InlineData(ArkEdition.ASA, "TheIsland", "TheIsland_WP")]
    [InlineData(ArkEdition.ASA, "TheIsland_WP", "TheIsland_WP")]
    [InlineData(ArkEdition.ASE, "TheIsland", "TheIsland")]
    [InlineData(ArkEdition.ASA, "CustomEntry", "CustomEntry")]
    public void ResolvesEditionEntryWithoutRewritingCustomMaps(ArkEdition edition, string input, string expected)
        => Assert.Equal(expected, ServerMapIdentifier.ForLaunch(edition, input));

    [Theory]
    [InlineData("TheIsland?listen?ServerPassword=secret")]
    [InlineData("../OtherMap")]
    [InlineData("/Game/Maps/Custom")]
    [InlineData("Map\n-server")]
    [InlineData("Map Name")]
    [InlineData("")]
    public void RejectsNamesThatCouldAddOptionsOrPaths(string name)
        => Assert.Throws<ArgumentException>(() => ServerMapIdentifier.ForLaunch(ArkEdition.ASA, name));

    [Fact]
    public void CatalogEntriesHaveUniqueValidIdentifiersAndReturnIndependentLists()
    {
        foreach (var edition in new[] { ArkEdition.ASE, ArkEdition.ASA })
        {
            var maps = ServerMapCatalog.ForEdition(edition);
            Assert.Equal(maps.Count, maps.Select(map => map.Identifier).Distinct().Count());
            Assert.All(maps, map =>
            {
                Assert.Equal(map.Identifier, ServerMapIdentifier.ForLaunch(edition, map.Identifier));
                Assert.Equal(map.Name, ServerMapCatalog.DisplayName(map.Identifier));
            });
            Assert.NotSame(maps, ServerMapCatalog.ForEdition(edition));
        }
    }
}
