using System.Net;
using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class CurseForgeMetadataTests
{
    [Theory]
    [InlineData(83374, true)]
    [InlineData(432, false)]
    public async Task VerifiesCatalogAndProjectGameAndUsesKeyOnlyInHeader(int projectGame, bool accepted)
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            Assert.Equal("https", request.RequestUri!.Scheme); Assert.Equal("api.curseforge.com", request.RequestUri.Host);
            Assert.DoesNotContain("fixture-key", request.RequestUri.ToString());
            Assert.Equal(new[] { "fixture-key" }, request.Headers.GetValues("x-api-key"));
            object content = request.RequestUri.AbsolutePath.Contains("/games/")
                ? new { data = new { id = 83374, slug = "ark-survival-ascended" } }
                : new { data = new { id = 123, gameId = projectGame, name = "Fixture", summary = "Project summary",
                    dateModified = "2026-01-01T00:00:00Z", links = new { websiteUrl = "https://www.curseforge.com/ark-survival-ascended/mods/fixture" },
                    logo = new { thumbnailUrl = "https://media.forgecdn.net/fixture.png" }, categories = new[] { new { name = "Structures" } } } };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(content)) };
        }));
        using var provider = new CurseForgeMetadata(http);
        if (accepted)
        {
            var result = await provider.GetAsync("123", "fixture-key");
            Assert.Equal(ArkEdition.ASA, result.Edition); Assert.Equal("123", result.Id);
            Assert.Equal("Project summary", result.Description); Assert.NotNull(result.PreviewImage);
        }
        else await Assert.ThrowsAsync<ModLookupException>(() => provider.GetAsync("123", "fixture-key"));
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ErrorsNeverEchoProviderResponseOrKey(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(_ => new(status) { Content = new StringContent("provider body fixture-key") }));
        using var provider = new CurseForgeMetadata(http);
        var error = await Assert.ThrowsAsync<ModLookupException>(() => provider.GetAsync("123", "fixture-key"));
        Assert.DoesNotContain("fixture-key", error.Message); Assert.DoesNotContain("provider body", error.Message);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
