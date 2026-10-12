using System.IO.Compression;
using System.Net;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class BootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-bootstrap-test-" + Guid.NewGuid().ToString("N"));
    private sealed class FixtureHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("https", request.RequestUri!.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    private static byte[] Archive(string name)
    {
        using var result = new MemoryStream();
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("test executable");
        return result.ToArray();
    }

    [Fact]
    public async Task ExistingToolIsReusedAndNewToolIsPublishedOnlyAfterExtraction()
    {
        var handler = new FixtureHandler(Archive("steamcmd.exe"));
        using var http = new HttpClient(handler);
        var bootstrap = new SteamCmdBootstrap(http);
        var target = Path.Combine(root, "tool");
        var exe = await bootstrap.EnsureAsync(target, ServerPlatform.Windows);
        Assert.Equal("test executable", await File.ReadAllTextAsync(exe));
        Assert.Equal(exe, await bootstrap.EnsureAsync(target, ServerPlatform.Windows));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("wrong.exe")]
    public async Task RejectsInvalidArchivesWithoutPublishingPartialInstallation(string entry)
    {
        using var http = new HttpClient(new FixtureHandler(Archive(entry)));
        var target = Path.Combine(root, "tool");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SteamCmdBootstrap(http).EnsureAsync(target, ServerPlatform.Windows));
        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
