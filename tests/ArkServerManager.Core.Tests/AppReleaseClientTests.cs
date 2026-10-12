using System.Net;
using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class AppReleaseClientTests
{
    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public async Task SelectsOnlyNewerStablePackageForCurrentPlatform(string runtime)
    {
        using var http = Client(Release(runtime));
        var releases = new AppReleaseClient(http);
        var update = await releases.FindUpdateAsync(new Version(1, 0, 0), runtime);
        Assert.Equal(new Version(1, 2, 3), update!.Version);
        Assert.Equal($"ARK-server-manager-{runtime}.zip", update.Asset.Name);
        Assert.Null(await releases.FindUpdateAsync(new Version(1, 2, 3), runtime));
        Assert.Null(await releases.FindUpdateAsync(new Version(2, 0, 0), runtime));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("foreign-link")]
    [InlineData("checksum")]
    [InlineData("duplicate")]
    [InlineData("wrong-platform")]
    public async Task RefusesUnreadyOrAmbiguousUpdate(string fault)
    {
        using var http = Client(Release("win-x64", fault));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppReleaseClient(http).FindUpdateAsync(new(1, 0, 0), "win-x64"));
    }

    [Fact]
    public async Task NoPublishedReleaseDoesNotRequireCredentials()
    {
        using var http = new HttpClient(new Handler(new(HttpStatusCode.NotFound)));
        Assert.Null(await new AppReleaseClient(http).FindUpdateAsync(new(1, 0, 0), "win-x64"));
    }

    private static string Release(string runtime, string fault = "")
    {
        var name = $"ARK-server-manager-{(fault == "wrong-platform" ? "linux-x64" : runtime)}.zip";
        var asset = new { name, state = "uploaded", size = 1234,
            digest = fault == "checksum" ? "" : "sha256:" + new string('a', 64),
            browser_download_url = fault == "foreign-link" ? "https://example.com/package.zip" : $"https://github.com/{AppReleaseClient.Repository}/releases/download/v1.2.3/{name}" };
        return JsonSerializer.Serialize(new { draft = fault == "draft", prerelease = fault == "prerelease", tag_name = "v1.2.3",
            html_url = $"https://github.com/{AppReleaseClient.Repository}/releases/tag/v1.2.3", assets = fault == "duplicate" ? new[] { asset, asset } : new[] { asset } });
    }
    private static HttpClient Client(string json) => new(new Handler(new(HttpStatusCode.OK) { Content = new StringContent(json) }));
    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal($"https://api.github.com/repos/{AppReleaseClient.Repository}/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(response.StatusCode) { Content = response.Content is null ? null : new StringContent(response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult()) });
        }
    }
}
