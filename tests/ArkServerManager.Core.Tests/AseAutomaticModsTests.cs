using System.Net;
using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class AseAutomaticModsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-auto-mods-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(346110, true)]
    [InlineData(123, false)]
    public async Task ExistingNativeToolIsReusedAndWorkshopGameMembershipIsChecked(int gameId, bool valid)
    {
        var binary = Path.Combine(root, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        await File.WriteAllTextAsync(binary, "recognition fixture, never executed");
        var folder = Path.Combine(root, "ShooterGame", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "GameUserSettings.ini"), "[ServerSettings]\nActiveMods=123\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "Game.ini"), "[ModInstaller]\nModIDS=123\n");
        var tool = Path.Combine(root, "Engine", "Binaries", "ThirdParty", "SteamCMD", "Win64", "steamcmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
        await File.WriteAllTextAsync(tool, "existing tool fixture; never executed");
        var handler = new MetadataHandler(gameId);
        using var http = new HttpClient(handler);
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root,
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }) { Mods = ["123"] };
        var installer = new AseAutomaticMods(http);
        if (valid) await installer.PrepareAsync(server, ServerPlatform.Windows, CancellationToken.None);
        else await Assert.ThrowsAsync<ModLookupException>(() => installer.PrepareAsync(server, ServerPlatform.Windows, CancellationToken.None));
        Assert.Equal(1, handler.Requests);
        Assert.Equal("existing tool fixture; never executed", await File.ReadAllTextAsync(tool));
        await File.WriteAllTextAsync(Path.Combine(folder, "Game.ini"), "[ModInstaller]\nModIDS=999\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.PrepareAsync(server, ServerPlatform.Windows, CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    private sealed class MetadataHandler(int gameId) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.Equal("api.steampowered.com", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { response = new { publishedfiledetails = new[] { new { publishedfileid = "123", result = 1, consumer_app_id = gameId, title = "Fixture", time_updated = 1 } } } })) });
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
