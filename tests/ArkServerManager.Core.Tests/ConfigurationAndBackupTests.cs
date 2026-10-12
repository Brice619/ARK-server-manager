using System.IO.Compression;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class ConfigurationAndBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-snapshot-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EditingPreservesUnknownSettingsCommentsRepeatedEntriesAndLineEndings()
    {
        var ini = new IniDocument("; keep\r\n[ServerSettings]\r\nMaxPlayers=10\r\nUnknown=hello=world\r\n[Mod]\r\nArray=one\r\nArray=two\r\n");
        ini.SetValues("ServerSettings", "MaxPlayers", ["70"]);
        Assert.Contains("; keep\r\n", ini.ToString());
        Assert.Equal(new[] { "hello=world" }, ini.GetValues("ServerSettings", "Unknown"));
        Assert.Equal(new[] { "one", "two" }, ini.GetValues("Mod", "Array"));
        Assert.Equal(new[] { "70" }, ini.GetValues("serversettings", "maxplayers"));
        ini.SetValues("Mod", "Array", ["three"]);
        Assert.Equal(new[] { "three" }, ini.GetValues("Mod", "Array"));
        Assert.Throws<ArgumentException>(() => ini.SetValues("Mod", "Injected\nKey", ["bad"]));
    }

    [Fact]
    public void RetentionCountsOnlyRuntimeAndKeepsTenHourlyPositions()
    {
        var clock = new RuntimeBackupClock();
        Assert.Empty(clock.Advance(TimeSpan.FromHours(20), false));
        Assert.Empty(clock.Advance(TimeSpan.FromMinutes(59), true));
        Assert.Single(clock.Advance(TimeSpan.FromMinutes(1), true));
        for (var i = 0; i < 10; i++) clock.Advance(TimeSpan.FromHours(1), true);
        Assert.Equal(11, clock.CompletedHours);
        Assert.False(clock.ShouldRetain(1));
        Assert.True(clock.ShouldRetain(2));
        Assert.True(clock.ShouldRetain(11));
        Assert.False(clock.ShouldRetain(12));
        Assert.Empty(clock.Advance(TimeSpan.FromDays(10), false));
        Assert.Equal(11, clock.CompletedHours);
    }

    [Fact]
    public async Task SharedClusterSnapshotCannotBeRestoredAsAMapEvenWithMatchingIdentity()
    {
        var state = Path.Combine(root, "Transfers");
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "survivor"), "shared transfer state");
        var archive = new SnapshotArchive();
        var id = Guid.NewGuid();
        var backup = Path.Combine(root, "Backups", "shared.zip");
        await archive.CreateAsync(state, backup, id, ArkEdition.ASE, 1, scope: SnapshotScope.SharedCluster);
        Assert.Equal(SnapshotScope.SharedCluster, (await archive.VerifyAsync(backup)).Scope);
        var target = Path.Combine(root, "restore");
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.ExtractToNewDirectoryAsync(backup, target, id, ArkEdition.ASE));
        Assert.False(Directory.Exists(target));
        await archive.ExtractToNewDirectoryAsync(backup, target, id, ArkEdition.ASE, expectedScope: SnapshotScope.SharedCluster);
        Assert.Equal("shared transfer state", await File.ReadAllTextAsync(Path.Combine(target, "survivor")));
    }

    [Fact]
    public async Task SnapshotPreservesNestedStateAndDetectsTampering()
    {
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(Path.Combine(state, "players"));
        await File.WriteAllTextAsync(Path.Combine(state, "world.ark"), "world data");
        await File.WriteAllTextAsync(Path.Combine(state, "players", "survivor.arkprofile"), "player data");
        var destination = Path.Combine(root, "backups", "hour-1.zip");
        var service = new SnapshotArchive();
        var serverId = Guid.NewGuid();
        var result = await service.CreateAsync(state, destination, serverId, ArkEdition.ASE, 1);
        Assert.Equal(2, result.Files.Count);
        Assert.Equal(serverId, (await service.VerifyAsync(destination)).ServerId);
        using (var zip = ZipFile.Open(destination, ZipArchiveMode.Update))
        {
            zip.GetEntry("state/world.ark")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("state/world.ark").Open());
            writer.Write("wrong data");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyAsync(destination));
    }

    [Fact]
    public async Task RestoreStagesExactStateAndRejectsWrongServer()
    {
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "world.ark"), "saved world");
        var service = new SnapshotArchive();
        var id = Guid.NewGuid();
        var backup = Path.Combine(root, "hour.zip");
        await service.CreateAsync(state, backup, id, ArkEdition.ASE, 1);
        var restore = Path.Combine(root, "restored");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExtractToNewDirectoryAsync(backup, restore, Guid.NewGuid(), ArkEdition.ASE));
        Assert.False(Directory.Exists(restore));
        await service.ExtractToNewDirectoryAsync(backup, restore, id, ArkEdition.ASE);
        Assert.Equal("saved world", await File.ReadAllTextAsync(Path.Combine(restore, "world.ark")));
        await Assert.ThrowsAsync<IOException>(() => service.ExtractToNewDirectoryAsync(backup, restore, id, ArkEdition.ASE));
    }

    [Fact]
    public async Task RestoreRejectsTraversalAndLeavesDestinationUntouched()
    {
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid();
        var backup = Path.Combine(root, "malicious.zip");
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("state/../outside").Open())) writer.Write("bad");
            var manifest = new SnapshotManifest(id, ArkEdition.ASE, 1, DateTimeOffset.UtcNow, [new("../outside", 3, "00")]);
            using var metadata = zip.CreateEntry("manifest.json").Open();
            System.Text.Json.JsonSerializer.Serialize(metadata, manifest);
        }
        var restore = Path.Combine(root, "restore");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SnapshotArchive().ExtractToNewDirectoryAsync(backup, restore, id, ArkEdition.ASE));
        Assert.False(Directory.Exists(restore));
        Assert.False(File.Exists(Path.Combine(root, "outside")));
    }

    [Fact]
    public async Task ArchiveNeverOverwritesAnExistingRestorePointOrIncludesItself()
    {
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "world.ark"), "world");
        var service = new SnapshotArchive();
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(state, Path.Combine(state, "self.zip"), Guid.NewGuid(), ArkEdition.ASA, 1));
        var destination = Path.Combine(root, "snapshot.zip");
        await service.CreateAsync(state, destination, Guid.NewGuid(), ArkEdition.ASA, 1);
        var before = await File.ReadAllBytesAsync(destination);
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(state, destination, Guid.NewGuid(), ArkEdition.ASA, 2));
        Assert.Equal(before, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
