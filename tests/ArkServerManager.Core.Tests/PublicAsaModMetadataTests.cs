using System.Net;
using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class PublicAsaModMetadataTests
{
    [Theory]
    [InlineData(123, "ark-survival-ascended", true)]
    [InlineData(124, "ark-survival-ascended", false)]
    [InlineData(123, "minecraft", false)]
    public async Task PublicLookupVerifiesIdAndGameWithoutCredentials(int returnedId, string game, bool valid)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https://api.cfwidget.com/123", request.RequestUri!.ToString());
            Assert.False(request.Headers.Contains("x-api-key"));
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = returnedId, game, title = "Fixture", summary = "Summary", thumbnail = "https://media.forgecdn.net/fixture.png",
                urls = new { curseforge = "https://www.curseforge.com/ark-survival-ascended/mods/fixture" },
                download = new { uploaded_at = "2026-07-09T22:48:50.77Z" }, categories = new[] { "Structures" }
            })) };
        }));
        var lookup = new PublicAsaModMetadata(http);
        if (valid)
        {
            var mod = await lookup.GetAsync("123");
            Assert.Equal("Fixture", mod.Name); Assert.Equal("Summary", mod.Description);
            Assert.Equal(ArkEdition.ASA, mod.Edition); Assert.NotNull(mod.PreviewImage);
        }
        else await Assert.ThrowsAsync<ModLookupException>(() => lookup.GetAsync("123"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task UnavailableLookupExplainsIdInstallationRemainsAvailable(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(_ => new(status)));
        var error = await Assert.ThrowsAsync<ModLookupException>(() => new PublicAsaModMetadata(http).GetAsync("123"));
        Assert.Contains("ID", error.Message);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }
}
