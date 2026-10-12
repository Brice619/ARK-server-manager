using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class DiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-discovery-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SearchFindsInstallationWithoutCopyingOrModifyingFiles()
    {
        var server = Path.Combine(root, "servers", "island");
        var binary = Path.Combine(server, "ShooterGame", "Binaries", "Win64", "ArkAscendedServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        await File.WriteAllTextAsync(binary, "fixture executable marker");
        var config = Path.Combine(server, "ShooterGame", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "GameUserSettings.ini"), "[ServerSettings]");
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
        var results = await new InstallationDiscovery().SearchAsync([root, server]);
        var candidate = Assert.Single(results.Candidates);
        Assert.Equal(ArkEdition.ASA, candidate.Edition);
        Assert.Equal(server, candidate.Directory);
        Assert.Single(candidate.ConfigurationFiles);
        Assert.Contains(candidate.Findings, text => text.Contains("still need validation"));
        Assert.Equal(before, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    [Fact]
    public async Task ReadsSteamLibrariesAndReportsManualSelectionFailure()
    {
        var steamapps = Path.Combine(root, "steamapps");
        Directory.CreateDirectory(steamapps);
        var library = Path.Combine(root, "other library");
        var escaped = library.Replace("\\", "\\\\");
        await File.WriteAllTextAsync(Path.Combine(steamapps, "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{escaped}\" }} }}");
        Assert.Contains(Path.Combine(library, "steamapps", "common"), InstallationDiscovery.SteamLibraryRoots(root));
        var result = new InstallationDiscovery().Evaluate(root);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Findings, text => text.Contains("No recognized"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
