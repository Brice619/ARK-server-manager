using System.Net;
using System.Security.Cryptography;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class AppReleaseDownloadTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-release-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("valid")]
    [InlineData("checksum")]
    [InlineData("oversize")]
    [InlineData("short")]
    [InlineData("existing")]
    public async Task VerifiesCompleteDownloadAndPreservesExistingFiles(string scenario)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "package.zip");
        var expected = new byte[] { 1, 2, 3 };
        var received = scenario switch { "checksum" => new byte[] { 3, 2, 1 }, "oversize" => new byte[] { 1, 2, 3, 4 }, "short" => new byte[] { 1 }, _ => expected };
        var uri = new Uri($"https://github.com/{AppReleaseClient.Repository}/releases/download/v1.2.3/ARK-server-manager-win-x64.zip");
        var release = new AppRelease(new(1, 2, 3), "v1.2.3", new($"https://github.com/{AppReleaseClient.Repository}/releases/tag/v1.2.3"),
            new("ARK-server-manager-win-x64.zip", uri, expected.Length, Convert.ToHexString(SHA256.HashData(expected))));
        if (scenario == "existing") await File.WriteAllTextAsync(path, "preserve");
        using var http = new HttpClient(new Handler(received));
        if (scenario == "valid")
        {
            await AppReleaseDownload.DownloadAsync(http, release, path);
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        }
        else if (scenario == "existing")
        {
            await Assert.ThrowsAsync<IOException>(() => AppReleaseDownload.DownloadAsync(http, release, path));
            Assert.Equal("preserve", await File.ReadAllTextAsync(path));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => AppReleaseDownload.DownloadAsync(http, release, path));
            Assert.False(File.Exists(path));
        }
    }

    private sealed class Handler(byte[] data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data), RequestMessage = request });
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
