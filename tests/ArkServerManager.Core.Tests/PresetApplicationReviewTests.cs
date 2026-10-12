using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class PresetApplicationReviewTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JoiningReviewPlansSharedDefaultsWithoutPublishingMembershipOrChangingFiles(bool existingCluster)
    {
        var f = await FixtureAsync();
        var target = new ClusterDefinition(Guid.NewGuid(), "Target", ArkEdition.ASE, f.Server.Settings with { Name = "Target", MaxPlayers = 80 })
        {
            Defaults = new("Shared", ArkEdition.ASE, f.Server.Settings with { MaxPlayers = 80 }, ["456"])
            { UserSettings = "[ServerSettings]\nTamingSpeedMultiplier=8\nServerAdminPassword=do-not-inherit\n" }
        };
        if (existingCluster) await f.Repository.ChangeAsync(c => c.AddCluster(target));
        var source = Path.Combine(f.Config, "GameUserSettings.ini");
        var originalFile = await File.ReadAllBytesAsync(source);
        var originalCatalog = await File.ReadAllBytesAsync(Path.Combine(root, "catalog.json"));
        var service = new PresetApplicationReviewService(f.Repository, f.Library);
        var prepared = await service.PrepareJoiningAsync(f.Server.Id, target, Guid.NewGuid());
        Assert.Equal(target.Id, prepared.Server.ClusterId);
        Assert.Equal(80, prepared.Plan.Settings.MaxPlayers);
        Assert.Equal(new[] { "456" }, prepared.Plan.Mods);
        Assert.Equal(f.Server.Settings.Name, prepared.Plan.Settings.Name);
        Assert.Equal(f.Server.Settings.GamePort, prepared.Plan.Settings.GamePort);
        Assert.Equal(f.Server.Settings.RconPort, prepared.Plan.Settings.RconPort);
        Assert.Contains("TamingSpeedMultiplier=8", prepared.Plan.UserSettings);
        Assert.Contains("ServerAdminPassword=private-admin", prepared.Plan.UserSettings);
        Assert.Contains("ServerPassword=private-join", prepared.Plan.UserSettings);
        Assert.DoesNotContain("do-not-inherit", JsonSerializer.Serialize(prepared.Review));
        Assert.Empty(prepared.Plan.OverridePolicy!.Overrides);
        Assert.False(prepared.Plan.OverridePolicy.MaxPlayers); Assert.False(prepared.Plan.OverridePolicy.Mods);
        var journal = new ClusterJoiningJournal(root);
        var accepted = new ClusterJoiningRecord(1, Guid.NewGuid(), target, !existingCluster, [f.Server], [prepared]);
        await journal.AcceptAsync(accepted);
        await journal.AcceptAsync(accepted);
        var recovered = Assert.Single(await new ClusterJoiningJournal(root).ReadAsync());
        Assert.Equal(accepted.OperationId, recovered.OperationId);
        Assert.Equal(prepared.Plan.UserSettings, recovered.Applications.Single().Plan.UserSettings);
        Assert.DoesNotContain("private-admin", recovered.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AcceptAsync(accepted with { OperationId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.AcceptAsync(accepted with
        { Originals = [f.Server with { Directory = f.Server.Directory + "-changed" }] }));
        Assert.Null((await f.Repository.ReadAsync()).Servers.Single().ClusterId);
        Assert.Equal(originalFile, await File.ReadAllBytesAsync(source));
        Assert.Equal(originalCatalog, await File.ReadAllBytesAsync(Path.Combine(root, "catalog.json")));
        Assert.False(File.Exists(Path.Combine(f.Config, "Game.ini")));
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-preset-review-" + Guid.NewGuid().ToString("N"));

    private async Task<(CatalogRepository Repository, PresetLibrary Library, ServerDefinition Server, PresetEntry Preset, string Config)> FixtureAsync()
    {
        var directory = Path.Combine(root, "server");
        var binary = Path.Combine(directory, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!); await File.WriteAllTextAsync(binary, "fixture");
        var config = Path.Combine(directory, "ShooterGame", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "GameUserSettings.ini"),
            "[ServerSettings]\nServerAdminPassword=private-admin\nServerPassword=private-join\nRCONEnabled=True\nRCONPort=32330\n");
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", directory,
            new() { Name = "Current", MaxPlayers = 20, GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        await repository.ChangeAsync(c => c.AddServer(server));
        var library = new PresetLibrary(Path.Combine(root, "presets.json"));
        var preset = await library.SaveAsync(new("Rates", ArkEdition.ASE, server.Settings with { MaxPlayers = 40 }, ["123"])
            { UserSettings = "[ServerSettings]\nTamingSpeedMultiplier=5\n" });
        return (repository, library, server, preset, config);
    }

    [Fact]
    public async Task ReviewHasNoPasswordsAndDoesNotCreateMissingGameFile()
    {
        var f = await FixtureAsync();
        var source = Path.Combine(f.Config, "GameUserSettings.ini");
        var before = await File.ReadAllBytesAsync(source);
        var service = new PresetApplicationReviewService(f.Repository, f.Library);
        var prepared = await service.PrepareAsync(new(f.Server.Id, f.Preset.Id));
        Assert.Equal(["123"], prepared.Review.AddedMods);
        Assert.Equal(40, prepared.Review.DesiredMaxPlayers);
        Assert.True(prepared.Review.ConfigurationChanges > 0);
        var serialized = JsonSerializer.Serialize(prepared.Review);
        Assert.DoesNotContain("private-admin", serialized); Assert.DoesNotContain("private-join", serialized);
        Assert.Null(prepared.GameSourceSHA256);
        Assert.False(File.Exists(Path.Combine(f.Config, "Game.ini")));
        Assert.Equal(before, await File.ReadAllBytesAsync(source));
        var rebuilt = await service.RebuildReviewedAsync(prepared.Review);
        Assert.Equal(prepared.Review.ReviewToken, rebuilt.Review.ReviewToken);
        Assert.DoesNotContain("private-admin", rebuilt.ToString());
    }

    [Theory]
    [InlineData("server")]
    [InlineData("preset")]
    [InlineData("user-file")]
    [InlineData("game-created")]
    public async Task ReviewCannotBeAppliedAfterItsSourceChanges(string change)
    {
        var f = await FixtureAsync();
        var service = new PresetApplicationReviewService(f.Repository, f.Library);
        var expected = (await service.PrepareAsync(new(f.Server.Id, f.Preset.Id))).Review;
        if (change == "server") await f.Repository.ChangeAsync(c => c.UpdateSettings(f.Server.Id, f.Server.Settings with { Name = "Changed" }));
        if (change == "preset") await f.Library.SaveAsync(f.Preset.Preset with { Name = "Changed preset" }, f.Preset.Id);
        if (change == "user-file") await File.AppendAllTextAsync(Path.Combine(f.Config, "GameUserSettings.ini"), "; external edit\n");
        if (change == "game-created") await File.WriteAllTextAsync(Path.Combine(f.Config, "Game.ini"), "[Custom]\nKeep=this\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RebuildReviewedAsync(expected));
    }

    [Fact]
    public async Task ReviewCountsRemovedLegacyModConfigurationEntries()
    {
        var repository = new CatalogRepository(Path.Combine(root, "asa-catalog.json"));
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASA, "TheIsland_WP", Path.Combine(root, "asa"),
            new() { Name = "ASA server", MaxPlayers = 20, GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        await repository.ChangeAsync(c => c.AddServer(server));
        var library = new PresetLibrary(Path.Combine(root, "asa-presets.json"));
        var preset = await library.SaveAsync(new("ASA preset", ArkEdition.ASA, server.Settings, []));
        var includeLegacy = false;
        var service = new PresetApplicationReviewService(repository, library, (id, file, ct) =>
        {
            var content = file == "Game.ini" ? "[ModInstaller]\n" + (includeLegacy ? "ModIDS=123\n" : "")
                : "[ServerSettings]\nRCONEnabled=True\n" + (includeLegacy ? "ActiveMods=123\n" : "");
            return Task.FromResult(new ServerConfigurationFile(id, file, "Windows", content, [])
                { SourceSHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))) });
        });
        var without = await service.PrepareAsync(new(server.Id, preset.Id));
        includeLegacy = true;
        var with = await service.PrepareAsync(new(server.Id, preset.Id));
        Assert.Equal(without.Review.ConfigurationChanges + 2, with.Review.ConfigurationChanges);
        Assert.Empty(new IniDocument(with.Plan.GameSettings).GetValues("ModInstaller", "ModIDS"));
        Assert.Empty(new IniDocument(with.Plan.UserSettings).GetValues("ServerSettings", "ActiveMods"));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
