using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class WorldSnapshotTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-world-snapshot-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ArkEdition.ASE)]
    [InlineData(ArkEdition.ASA)]
    public async Task WorldLayoutIncludesWorldPlayersAndTribesButExcludesSettings(ArkEdition edition)
    {
        var saved = Path.Combine(root, "Saved");
        foreach (var file in new[] { "SavedArks/Map/world.ark", "SavedArks/Map/tribe.arktribe", "SaveGames/player.arkprofile",
            "Config/WindowsServer/GameUserSettings.ini", "Logs/server.log", "clusters/transfer.bin" })
        {
            var path = Path.Combine(saved, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, file);
        }
        var id = Guid.NewGuid(); var archive = Path.Combine(root, "world.zip");
        var manifest = await new SnapshotArchive().CreateAsync(saved, archive, id, edition, 1, layout: SnapshotLayout.WorldSavedFolders);
        Assert.Equal(SnapshotLayout.WorldSavedFolders, manifest.Layout);
        Assert.Equal(3, manifest.Files.Count);
        Assert.DoesNotContain(manifest.Files, file => file.Path.Contains("Config") || file.Path.Contains("Logs") || file.Path.Contains("clusters"));
        var restored = Path.Combine(root, "extracted");
        await new SnapshotArchive().ExtractToNewDirectoryAsync(archive, restored, id, edition, expectedRuntimeHour: 1);
        Assert.True(File.Exists(Path.Combine(restored, "SavedArks", "Map", "world.ark")));
        Assert.True(File.Exists(Path.Combine(restored, "SaveGames", "player.arkprofile")));
        Assert.False(Directory.Exists(Path.Combine(restored, "Config")));
    }

    [Fact]
    public async Task ConfigurationAloneIsNotAnEmptySuccessfulWorldBackup()
    {
        var saved = Path.Combine(root, "Saved"); Directory.CreateDirectory(Path.Combine(saved, "Config"));
        await File.WriteAllTextAsync(Path.Combine(saved, "Config", "Game.ini"), "configuration");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SnapshotArchive().CreateAsync(saved,
            Path.Combine(root, "world.zip"), Guid.NewGuid(), ArkEdition.ASE, 1, layout: SnapshotLayout.WorldSavedFolders));
        Assert.False(File.Exists(Path.Combine(root, "world.zip")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
