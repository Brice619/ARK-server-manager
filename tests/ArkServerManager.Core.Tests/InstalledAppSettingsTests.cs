using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class InstalledAppSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-installed-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingConfigurationDoesNotCreateFolders()
    {
        Assert.Null(InstalledAppSettingsFile.Read(root));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void LoadsChosenFolderWithoutChangingConfiguration()
    {
        Directory.CreateDirectory(root);
        var servers = Path.Combine(root, "Chosen servers");
        Directory.CreateDirectory(servers);
        var path = Path.Combine(root, InstalledAppSettingsFile.FileName);
        var json = JsonSerializer.Serialize(new InstalledAppSettings(1, servers));
        File.WriteAllText(path, json);
        Assert.Equal(servers, InstalledAppSettingsFile.Read(root)!.ServerRoot);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":2,\"ServerRoot\":\"relative\"}")]
    [InlineData("{\"SchemaVersion\":1,\"ServerRoot\":\"relative\"}")]
    [InlineData("{\"SchemaVersion\":1,\"ServerRoot\":null}")]
    [InlineData("null")]
    public void RejectsInvalidSettings(string json)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, InstalledAppSettingsFile.FileName), json);
        Assert.Throws<InvalidDataException>(() => InstalledAppSettingsFile.Read(root));
    }

    [Fact]
    public void RejectsOversizedSettings()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, InstalledAppSettingsFile.FileName), new string(' ', 16385));
        Assert.Throws<InvalidDataException>(() => InstalledAppSettingsFile.Read(root));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
