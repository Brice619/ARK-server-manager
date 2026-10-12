using System.Text.Json.Nodes;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class RemovedAsaModFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-asa-cleanup-" + Guid.NewGuid().ToString("N"));
    private string Native => Path.Combine(root, "ShooterGame", "Binaries", "Win64", "ShooterGame");
    private string Mods => Path.Combine(Native, "Mods", "83374");
    private string Library => Path.Combine(Native, "ModsUserData", "83374", "library.json");
    private ServerDefinition Server => new(Guid.NewGuid(), ArkEdition.ASA, "TheIsland_WP", root,
        new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 }) { Mods = ["928708", "123"] };

    private async Task<JsonObject> FixtureAsync()
    {
        foreach (var folder in new[] { "928708_6601211", "928708_100", "123_10", ".temp/928708_900", ".temp/123_20", "928708_other" })
        {
            var path = Path.Combine(Mods, folder, "content.pak"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "fixture");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Library)!);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(Library)!, "shared_info.json"), "shared state");
        var user = Path.Combine(Path.GetDirectoryName(Library)!, "local", "user_info.json");
        Directory.CreateDirectory(Path.GetDirectoryName(user)!); await File.WriteAllTextAsync(user, "private user state");
        var world = Path.Combine(root, "ShooterGame", "Saved", "SavedArks", "TheIsland_WP.ark");
        Directory.CreateDirectory(Path.GetDirectoryName(world)!); await File.WriteAllTextAsync(world, "world state");
        var library = JsonNode.Parse("""
            {"unknownRoot":{"keep":true},"installedMods":[
            {"iD":"INTERNAL-GUID","details":{"iD":928708,"gameId":83374},"installedFile":{"modId":928708,"iD":6601211,"gameId":83374},"pathOnDisk":"83374/928708_6601211","unmanaged":false,"downloadInfo":{"tempFile":""}},
            {"iD":"SIBLING-GUID","details":{"iD":123,"gameId":83374},"installedFile":{"modId":123,"iD":10,"gameId":83374},"pathOnDisk":"83374/123_10","unmanaged":false,"keepSibling":"unchanged"}
            ]}
            """)!.AsObject();
        await File.WriteAllTextAsync(Library, library.ToJsonString());
        return library;
    }

    [Fact]
    public async Task RemovesSelectedVersionsAndTemporaryFoldersPreservingSiblingAndSharedState()
    {
        await FixtureAsync();
        await RemovedAsaModFiles.DeleteAsync(Server, ["928708"], CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(Mods, "928708_6601211")));
        Assert.False(Directory.Exists(Path.Combine(Mods, "928708_100")));
        Assert.False(Directory.Exists(Path.Combine(Mods, ".temp", "928708_900")));
        Assert.True(File.Exists(Path.Combine(Mods, "123_10", "content.pak")));
        Assert.True(File.Exists(Path.Combine(Mods, ".temp", "123_20", "content.pak")));
        Assert.True(Directory.Exists(Path.Combine(Mods, "928708_other")));
        var library = JsonNode.Parse(await File.ReadAllTextAsync(Library))!;
        Assert.True(library["unknownRoot"]!["keep"]!.GetValue<bool>());
        Assert.Equal("unchanged", Assert.Single(library["installedMods"]!.AsArray())!["keepSibling"]!.GetValue<string>());
        Assert.Equal("shared state", await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(Library)!, "shared_info.json")));
        Assert.Equal("private user state", await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(Library)!, "local", "user_info.json")));
        Assert.Equal("world state", await File.ReadAllTextAsync(Path.Combine(root, "ShooterGame", "Saved", "SavedArks", "TheIsland_WP.ark")));
        await RemovedAsaModFiles.DeleteAsync(Server, ["928708"], CancellationToken.None);
        Assert.Single(JsonNode.Parse(await File.ReadAllTextAsync(Library))!["installedMods"]!.AsArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeLibraryUtf8PreambleIsAcceptedAndPreserved(bool bom)
    {
        var library = await FixtureAsync();
        await File.WriteAllTextAsync(Library, library.ToJsonString(), new System.Text.UTF8Encoding(bom));
        await RemovedAsaModFiles.DeleteAsync(Server, ["928708"], CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(Library);
        Assert.Equal(bom, bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.Single(JsonNode.Parse(await File.ReadAllTextAsync(Library))!["installedMods"]!.AsArray());
    }

    [Theory]
    [InlineData("pathOnDisk", "../../Saved")]
    [InlineData("pathOnDisk", "83374/123_10")]
    [InlineData("unmanaged", "true")]
    [InlineData("tempFile", "unfinished.zip")]
    public async Task UnsupportedSelectedEntryIsRefusedBeforeAnyDeletion(string field, string value)
    {
        var library = await FixtureAsync(); var item = library["installedMods"]![0]!;
        if (field == "unmanaged") item[field] = true;
        else if (field == "tempFile") item["downloadInfo"]![field] = value;
        else item[field] = value;
        await File.WriteAllTextAsync(Library, library.ToJsonString());
        var before = await File.ReadAllBytesAsync(Library);
        await Assert.ThrowsAnyAsync<Exception>(() => RemovedAsaModFiles.DeleteAsync(Server, ["928708"], CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(Library));
        Assert.True(File.Exists(Path.Combine(Mods, "928708_6601211", "content.pak")));
        Assert.True(File.Exists(Path.Combine(Mods, "928708_100", "content.pak")));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
