using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class AseAutomaticModsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-auto-mods-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UnchangedModsReuseNativeToolWithoutOnlineCompatibilityRequests()
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
        var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root,
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }) { Mods = ["123"] };
        var installer = new AseAutomaticMods(http);
        await installer.PrepareAsync(server, ServerPlatform.Windows, CancellationToken.None);
        Assert.Equal(0, handler.Requests);
        Assert.Equal("existing tool fixture; never executed", await File.ReadAllTextAsync(tool));
        await File.WriteAllTextAsync(Path.Combine(folder, "Game.ini"), "[ModInstaller]\nModIDS=999\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.PrepareAsync(server, ServerPlatform.Windows, CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            throw new InvalidOperationException("Startup must not make Workshop metadata requests for unchanged mods.");
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
