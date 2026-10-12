using System.IO.Compression;
using System.Net;
using System.Formats.Tar;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class SteamCmdBootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-bootstrap-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LinuxRepairsExecutablePermissionWithoutDownloadingOrReplacingContent()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(Path.Combine(root, "linux32"));
        foreach (var name in new[] { "steamcmd.sh", "linux32/steamcmd", "linux32/libstdc++.so.6" })
        {
            var path = Path.Combine(root, name);
            await File.WriteAllTextAsync(path, "existing tool");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        using var http = new HttpClient(new Handler(() => throw new InvalidOperationException("No download expected.")));
        await new SteamCmdBootstrap(http).EnsureAsync(root, ServerPlatform.Linux);
        foreach (var name in new[] { "steamcmd.sh", "linux32/steamcmd" })
        {
            var path = Path.Combine(root, name);
            Assert.Equal("existing tool", await File.ReadAllTextAsync(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
        }
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, "linux32/libstdc++.so.6")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LinuxLauncherAloneIsNotConsideredAnInstalledTool(bool completeArchive)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "steamcmd.sh"), "existing launcher");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(root, "steamcmd.sh"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
            foreach (var name in completeArchive ? new[] { "steamcmd.sh", "linux32/steamcmd", "linux32/libstdc++.so.6" } : new[] { "steamcmd.sh" })
            {
                using var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("bootstrap"));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                    { DataStream = data, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute });
            }
        var requests = 0;
        using var http = new HttpClient(new Handler(() => { requests++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) }; }));
        var bootstrap = new SteamCmdBootstrap(http);
        if (completeArchive)
        {
            await bootstrap.EnsureAsync(root, ServerPlatform.Linux);
            await bootstrap.EnsureAsync(root, ServerPlatform.Linux);
            Assert.Equal("bootstrap", await File.ReadAllTextAsync(Path.Combine(root, "linux32", "steamcmd")));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => bootstrap.EnsureAsync(root, ServerPlatform.Linux));
            Assert.False(Directory.Exists(Path.Combine(root, "linux32")));
        }
        Assert.Equal(1, requests);
        Assert.Equal("existing launcher", await File.ReadAllTextAsync(Path.Combine(root, "steamcmd.sh")));
    }

    [Fact]
    public async Task RepairsEmptyExecutableAndSerializesConcurrentPreparation()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "steamcmd.exe"), []);
        var requests = 0;
        using var http = new HttpClient(new Handler(() => { Interlocked.Increment(ref requests); return Archive(); }));
        var bootstrap = new SteamCmdBootstrap(http);
        var paths = await Task.WhenAll(bootstrap.EnsureAsync(root, ServerPlatform.Windows), bootstrap.EnsureAsync(root, ServerPlatform.Windows));
        Assert.Equal(paths[0], paths[1]); Assert.Equal(1, requests);
        Assert.Equal("bootstrap", await File.ReadAllTextAsync(paths[0]));
    }

    [Fact]
    public async Task RepairsMissingBootstrapFilesWithoutReplacingWorkshopCacheOrExistingLibraries()
    {
        Directory.CreateDirectory(root);
        var cache = Path.Combine(root, "steamapps", "workshop", "content", "346110", "123");
        Directory.CreateDirectory(cache);
        await File.WriteAllTextAsync(Path.Combine(cache, "asset"), "downloaded mod");
        await File.WriteAllTextAsync(Path.Combine(root, "existing.dll"), "self updated library");
        var requests = 0;
        using var http = new HttpClient(new Handler(() => { requests++; return Archive(); }));
        var bootstrap = new SteamCmdBootstrap(http);
        var executable = await bootstrap.EnsureAsync(root, ServerPlatform.Windows);
        Assert.Equal("bootstrap", await File.ReadAllTextAsync(executable));
        Assert.Equal("self updated library", await File.ReadAllTextAsync(Path.Combine(root, "existing.dll")));
        Assert.Equal("downloaded mod", await File.ReadAllTextAsync(Path.Combine(cache, "asset")));
        Assert.Equal(executable, await bootstrap.EnsureAsync(root, ServerPlatform.Windows));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task FailedDownloadLeavesExistingToolStateUntouched()
    {
        Directory.CreateDirectory(root);
        var cache = Path.Combine(root, "keep.dat"); await File.WriteAllTextAsync(cache, "preserve");
        using var http = new HttpClient(new Handler(() => new(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new SteamCmdBootstrap(http).EnsureAsync(root, ServerPlatform.Windows));
        Assert.Equal("preserve", await File.ReadAllTextAsync(cache));
        Assert.False(File.Exists(Path.Combine(root, "steamcmd.exe")));
    }

    private static HttpResponseMessage Archive()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var name in new[] { "steamcmd.exe", "existing.dll" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("bootstrap");
            }
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) };
    }
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response());
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
