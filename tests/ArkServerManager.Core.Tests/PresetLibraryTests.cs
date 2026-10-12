using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class PresetLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-presets-" + Guid.NewGuid().ToString("N"));
    private PresetLibrary Library => new(Path.Combine(root, "presets.json"));
    private static ServerPreset Preset(string name) => new(name, ArkEdition.ASE, new()
    { Name = "Server", GamePort = 7777, QueryPort = 27015, RconPort = 32330, Values = new() { ["Mod.Option"] = "Enabled" } }, ["731604991"]);

    [Fact]
    public void UnrecognizedPresetFieldsAreRejectedRatherThanSilentlyDroppingConfiguration()
    {
        var json = JsonSerializer.Serialize(new PresetExportDocument(1, Preset("Fixture")));
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["Preset"]!["UnrecognizedModConfiguration"] = "must not be dropped";
        Assert.Throws<InvalidDataException>(() => PresetLibrary.ReadImport(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())));
        node["Preset"]!.AsObject().Remove("UnrecognizedModConfiguration");
        node["Preset"]!["Settings"]!["UnrecognizedSetting"] = true;
        Assert.Throws<InvalidDataException>(() => PresetLibrary.ReadImport(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())));
        var valid = PresetLibrary.ReadImport(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal("Enabled", valid.Settings.Values["Mod.Option"]);
    }

    [Fact]
    public async Task EditingRenamingAndDeletingSurviveReloadWithoutMutatingInputs()
    {
        var input = Preset("Original");
        var entry = await Library.SaveAsync(input);
        input.Settings.Values.Clear();
        input.Mods.Clear();
        var reloaded = Assert.Single(await Library.ListAsync());
        Assert.Single(reloaded.Preset.Mods);
        Assert.Equal("Enabled", reloaded.Preset.Settings.Values["Mod.Option"]);
        await Library.RenameAsync(entry.Id, "Renamed");
        await Library.SaveAsync(Preset("Edited") with { Mods = ["123"] }, entry.Id);
        Assert.Equal("Edited", Assert.Single(await Library.ListAsync()).Preset.Name);
        Assert.True(await Library.DeleteAsync(entry.Id));
        Assert.Empty(await Library.ListAsync());
    }

    [Fact]
    public async Task ImportPreviewDoesNotChangeLibraryAndRoundTripRetainsModSettingsAndOrder()
    {
        var original = await Library.SaveAsync(Preset("Export") with { Mods = ["456", "123"] });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(await Library.ExportAsync(original.Id));
        var imported = PresetLibrary.ReadImport(bytes);
        Assert.Equal(new[] { "456", "123" }, imported.Mods);
        Assert.Equal("Enabled", imported.Settings.Values["Mod.Option"]);
        Assert.Single(await Library.ListAsync());
        var saved = await Library.SaveAsync(imported);
        Assert.NotEqual(original.Id, saved.Id);
        Assert.Equal(2, (await Library.ListAsync()).Count);
        Assert.Throws<InvalidDataException>(() => PresetLibrary.ReadImport("{\"SchemaVersion\":2}"u8));
        Assert.Throws<InvalidDataException>(() => PresetLibrary.ReadImport("{\"SchemaVersion\":1,\"Preset\":null}"u8));
    }

    [Fact]
    public async Task RetriedImportAddsOneEntryAndRejectsChangedContents()
    {
        var id = Guid.NewGuid();
        var first = await Library.ImportAsNewAsync(id, Preset("Imported"));
        var repeated = await Library.ImportAsNewAsync(id, Preset("Imported"));
        Assert.Equal(first.Id, repeated.Id);
        Assert.Single(await Library.ListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => Library.ImportAsNewAsync(id, Preset("Different")));
        Assert.Equal("Imported", Assert.Single(await Library.ListAsync()).Preset.Name);
        await Assert.ThrowsAsync<ArgumentException>(() => Library.ImportAsNewAsync(Guid.Empty, Preset("Invalid")));
    }

    [Fact]
    public async Task ExportStripsJoinAndAdminPasswordsWithoutChangingSavedPreset()
    {
        var input = Preset("Share");
        input.Settings.Values["serverpassword"] = "join-secret";
        input.Settings.Values["ServerAdminPassword"] = "admin-secret";
        input.Settings.Values["ADMINPASSWORD"] = "legacy-secret";
        var saved = await Library.SaveAsync(input);
        var exported = await Library.ExportAsync(saved.Id);
        var json = JsonSerializer.Serialize(exported);
        Assert.DoesNotContain("join-secret", json);
        Assert.DoesNotContain("admin-secret", json);
        Assert.DoesNotContain("legacy-secret", json);
        Assert.Equal("Enabled", exported.Preset.Settings.Values["Mod.Option"]);
        var reloaded = Assert.Single(await Library.ListAsync());
        Assert.Equal("join-secret", reloaded.Preset.Settings.Values["serverpassword"]);
        Assert.Equal("admin-secret", reloaded.Preset.Settings.Values["ServerAdminPassword"]);
        Assert.Equal("legacy-secret", input.Settings.Values["ADMINPASSWORD"]);
    }

    [Fact]
    public async Task ConcurrentWritersDoNotLoseEntriesAndInvalidEditLeavesExistingData()
    {
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Library.SaveAsync(Preset("Preset " + i))));
        var entries = await Library.ListAsync();
        Assert.Equal(12, entries.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => Library.RenameAsync(entries[0].Id, ""));
        Assert.Equal(entries[0].Preset.Name, (await Library.ListAsync()).Single(e => e.Id == entries[0].Id).Preset.Name);
        await Assert.ThrowsAsync<ArgumentException>(() => Library.SaveAsync(Preset("Missing"), Guid.NewGuid()));
        Assert.Equal(12, (await Library.ListAsync()).Count);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task CaptureReadsActualConfigurationAndRefusesChangedServer()
    {
        var binary = Path.Combine(root, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        await File.WriteAllTextAsync(binary, "recognition fixture, never executed");
        var folder = Path.Combine(root, "ShooterGame", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(folder);
        const string user = "[ServerSettings]\nServerAdminPassword=private\n[ModSection]\nArray=one\nArray=two\n";
        const string game = "[/Script/ShooterGame.ShooterGameMode]\nArray=first\nArray=second\n";
        await File.WriteAllTextAsync(Path.Combine(folder, "GameUserSettings.ini"), user);
        await File.WriteAllTextAsync(Path.Combine(folder, "Game.ini"), game);
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var expected = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root, Preset("Source").Settings)
            { Mods = ["731604991"] };
        await repository.ChangeAsync(c => c.AddServer(expected));
        var captured = await PresetCapture.ReadAsync(repository, new(expected, "Captured"), CancellationToken.None);
        Assert.Equal(user, captured.UserSettings); Assert.Equal(game, captured.GameSettings);
        Assert.Equal(expected.Mods, captured.Mods);
        await repository.ChangeAsync(c => c.UpdateSettings(expected.Id, expected.Settings with { MaxPlayers = 15 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PresetCapture.ReadAsync(repository, new(expected, "Stale"), CancellationToken.None));
        Assert.Equal(user, await File.ReadAllTextAsync(Path.Combine(folder, "GameUserSettings.ini")));
    }

    [Fact]
    public async Task ConfigurationTemplatesRetainRepeatedModSettingsAndExportRemovesPasswords()
    {
        var preset = Preset("Full configuration") with
        {
            UserSettings = "[ServerSettings]\nServerPassword=join-secret\nServerAdminPassword=admin-secret\n[ModSection]\nRate=3\nRate=4\nAdminPassword=legacy-secret\n",
            GameSettings = "[/Script/ShooterGame.ShooterGameMode]\nCustomEntry=first\nCustomEntry=second\n"
        };
        preset.Settings.Values["GameUserSettings.ini::ServerSettings::ServerAdminPassword"] = "qualified-secret";
        var entry = await Library.SaveAsync(preset);
        var reloaded = Assert.Single(await Library.ListAsync());
        Assert.Equal(preset.UserSettings, reloaded.Preset.UserSettings);
        Assert.Equal(preset.GameSettings, reloaded.Preset.GameSettings);
        var exported = await Library.ExportAsync(entry.Id);
        var json = JsonSerializer.Serialize(exported);
        Assert.DoesNotContain("join-secret", json); Assert.DoesNotContain("admin-secret", json); Assert.DoesNotContain("legacy-secret", json);
        Assert.DoesNotContain("qualified-secret", json);
        Assert.Equal(new[] { "3", "4" }, new IniDocument(exported.Preset.UserSettings!).GetValues("ModSection", "Rate"));
        Assert.Equal(preset.GameSettings, PresetLibrary.ReadImport(JsonSerializer.SerializeToUtf8Bytes(exported)).GameSettings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Library.EditReviewedAsync(entry with
            { Preset = entry.Preset with { GameSettings = "[Changed]\nValue=1\n" } }, preset with { Name = "Stale" }));
    }

    [Fact]
    public async Task ReviewedEditRetriesWithoutDuplicatesAndRejectsStaleOverwrite()
    {
        var entry = await Library.SaveAsync(Preset("Original"));
        var replacement = entry.Preset with { Name = "Edited", Mods = ["second", "first"],
            Settings = entry.Preset.Settings with { MaxPlayers = 12 } };
        var saved = await Library.EditReviewedAsync(entry, replacement);
        Assert.Equal(entry.Id, saved.Id);
        var replay = await Library.EditReviewedAsync(entry, replacement);
        Assert.Equal(saved.Id, replay.Id);
        Assert.Single(await Library.ListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Library.EditReviewedAsync(entry, Preset("Stale overwrite")));
        var current = Assert.Single(await Library.ListAsync());
        Assert.Equal("Edited", current.Preset.Name);
        Assert.Equal(new[] { "second", "first" }, current.Preset.Mods);
        Assert.Equal(12, current.Preset.Settings.MaxPlayers);
        Assert.Equal("Enabled", current.Preset.Settings.Values["Mod.Option"]);
    }
}
