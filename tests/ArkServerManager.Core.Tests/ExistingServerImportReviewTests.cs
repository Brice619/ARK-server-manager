using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ExistingServerImportReviewTests
{
    [Fact]
    public async Task ReviewPreservesFilesExcludesPasswordsBindsChangesAndRefusesManagedOverlap()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-existing-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            var binary = Path.Combine(source, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!); await File.WriteAllTextAsync(binary, "fixture; never launched");
            var config = Path.Combine(source, "ShooterGame", "Saved", "Config", "WindowsServer"); Directory.CreateDirectory(config);
            var settings = Path.Combine(config, "GameUserSettings.ini");
            await File.WriteAllTextAsync(settings, "[SessionSettings]\nSessionName=Existing name\n[ServerSettings]\nServerAdminPassword=private-admin\nServerPassword=private-join\nRCONEnabled=True\nRCONPort=32340\nMaxPlayers=40\nActiveMods=123,456\n");
            var worlds = Path.Combine(source, "ShooterGame", "Saved", "SavedArks"); Directory.CreateDirectory(worlds);
            await File.WriteAllTextAsync(Path.Combine(worlds, "TheIsland.ark"), "world state");
            var before = await File.ReadAllBytesAsync(settings);
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var service = new ExistingServerImportReviewService(repository);
            var request = new ExistingServerImportReviewRequest(source, ArkEdition.ASE, "Windows");
            var review = await service.ReadAsync(request);
            Assert.Equal("Existing name", review.Name); Assert.Equal(40, review.MaxPlayers); Assert.Equal(32340, review.RconPort);
            Assert.Equal(new[] { "TheIsland" }, review.SavedWorlds); Assert.Equal(new[] { "123", "456" }, review.ConfiguredMods);
            Assert.True(review.AdminPasswordConfigured); Assert.True(review.RconEnabled);
            Assert.DoesNotContain("private-admin", JsonSerializer.Serialize(review)); Assert.DoesNotContain("private-join", JsonSerializer.Serialize(review));
            Assert.Equal(before, await File.ReadAllBytesAsync(settings));
            Assert.False(File.Exists(Path.Combine(config, "Game.ini"))); Assert.False(File.Exists(Path.Combine(root, "catalog.json")));
            Assert.Equal(review.Token, (await service.ReadAsync(request)).Token);
            await File.AppendAllTextAsync(settings, "TamingSpeedMultiplier=8\n");
            Assert.NotEqual(review.Token, (await service.ReadAsync(request)).Token);
            await repository.ChangeAsync(c => c.AddServer(new(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", source,
                new() { Name = "Managed", GamePort = 7777, QueryPort = 27015, RconPort = 32340 })));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadAsync(request));
            await Assert.ThrowsAsync<ArgumentException>(() => service.ReadAsync(request with { Directory = "relative" }));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
