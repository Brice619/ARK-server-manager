using System.Net;
using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class ModTests
{
    private sealed class FixtureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }

    private static string Metadata(uint app = SteamWorkshopMetadata.AseGameId, int result = 1) => JsonSerializer.Serialize(new
    {
        response = new { publishedfiledetails = new[] { new
        {
            publishedfileid = "123456", result, consumer_app_id = app, title = "Example Mod",
            description = "Description", preview_url = "https://example.com/preview.png", time_updated = 1700000000,
            tags = new[] { new { tag = "Structures" } }
        } } }
    });

    [Fact]
    public async Task WorkshopLookupPostsIdAndReturnsDisplayMetadata()
    {
        using var http = new HttpClient(new FixtureHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("api.steampowered.com", request.RequestUri!.Host);
            var form = await request.Content!.ReadAsStringAsync();
            Assert.Contains("itemcount=1", form);
            Assert.Contains("publishedfileids%5B0%5D=123456", form);
            return new(HttpStatusCode.OK) { Content = new StringContent(Metadata()) };
        }));
        var mod = await new SteamWorkshopMetadata(http).GetAsync("123456");
        Assert.Equal("Example Mod", mod.Name);
        Assert.Equal(ArkEdition.ASE, mod.Edition);
        Assert.Equal("Structures", Assert.Single(mod.Tags));
        Assert.NotNull(mod.PreviewImage);
    }

    [Theory]
    [InlineData(999, 1)]
    [InlineData(346110, 9)]
    public async Task RejectsUnavailableOrWrongGameItems(uint app, int result)
    {
        using var http = new HttpClient(new FixtureHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(Metadata(app, result)) })));
        await Assert.ThrowsAsync<ModLookupException>(() => new SteamWorkshopMetadata(http).GetAsync("123456"));
    }

    [Fact]
    public void ModOrderCannotDropDuplicateOrInsertItems()
    {
        var catalog = new ServerCatalog();
        var id = Guid.NewGuid();
        catalog.AddServer(new(id, ArkEdition.ASE, "TheIsland", "server", new()
        { Name = "Server", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }));
        ModMetadata Mod(string modId) => new(modId, ArkEdition.ASE, modId, "", null, new Uri("https://example.com"), DateTimeOffset.UtcNow, []);
        catalog.AddMod(id, Mod("1"));
        catalog.AddMod(id, Mod("2"));
        Assert.Throws<ArgumentException>(() => catalog.AddMod(id, Mod("1")));
        Assert.Throws<ArgumentException>(() => catalog.SetModOrder(id, ["1", "1"]));
        Assert.Throws<ArgumentException>(() => catalog.SetModOrder(id, ["3", "2"]));
        catalog.SetModOrder(id, ["2", "1"]);
        Assert.Equal(new[] { "2", "1" }, Assert.Single(catalog.Servers).Mods);
        catalog.RemoveMod(id, "2");
        Assert.Equal("1", Assert.Single(Assert.Single(catalog.Servers).Mods));
    }
}
