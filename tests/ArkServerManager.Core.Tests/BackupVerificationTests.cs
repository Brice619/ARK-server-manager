using System.IO.Compression;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class BackupVerificationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-backup-verification-" + Guid.NewGuid().ToString("N"));
    private readonly Guid id = Guid.NewGuid();
    private CatalogRepository Repository => new(Path.Combine(root, "catalog.json"));

    private async Task<string> Seed(bool wrongServer = false, SnapshotScope scope = SnapshotScope.Server)
    {
        var server = Path.Combine(root, "server");
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", server,
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 })));
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "world.ark"), "fixture state");
        var archive = Path.Combine(server, "backups", "hour-00000001.zip");
        var manifest = await new SnapshotArchive().CreateAsync(state, archive, wrongServer ? Guid.NewGuid() : id, ArkEdition.ASE, 1, scope: scope);
        await new AtomicJsonStore<AutomaticBackupState>(Path.Combine(server, "backups", "backup-history.json"))
            .SaveAsync(new(1, id, ArkEdition.ASE, TimeSpan.FromHours(1).Ticks, null, null,
                [new(1, "hour-00000001.zip", manifest.CreatedAt)], null));
        return archive;
    }

    [Fact]
    public async Task SharedClusterArchiveCannotBeVerifiedAsAMapRestorePoint()
    {
        var path = await Seed(scope: SnapshotScope.SharedCluster);
        var before = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupVerification.VerifyAsync(Repository, new(id, 1)));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task VerifiesRecordedArchiveWithoutChangingIt()
    {
        var archive = await Seed();
        var original = await File.ReadAllBytesAsync(archive);
        var result = await BackupVerification.VerifyAsync(Repository, new(id, 1));
        Assert.Equal(id, result.ServerId);
        Assert.Equal(1, result.RuntimeHour);
        Assert.Equal(1, result.FileCount);
        Assert.Equal(13, result.TotalBytes);
        Assert.Equal(original, await File.ReadAllBytesAsync(archive));
    }

    [Fact]
    public async Task RejectsUnrecordedPointAndAnotherServersValidArchive()
    {
        await Seed(wrongServer: true);
        await Assert.ThrowsAsync<ArgumentException>(() => BackupVerification.VerifyAsync(Repository, new(id, 2)));
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupVerification.VerifyAsync(Repository, new(id, 1)));
    }

    [Fact]
    public async Task CorruptedContentCannotBecomeVerified()
    {
        var archive = await Seed();
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            zip.GetEntry("state/world.ark")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("state/world.ark").Open());
            writer.Write("changed state");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupVerification.VerifyAsync(Repository, new(id, 1)));
    }

    [Theory]
    [InlineData("missing-inventory")]
    [InlineData("null-file")]
    [InlineData("bad-checksum")]
    [InlineData("negative-length")]
    public async Task MalformedMetadataFailsBeforeCreatingRestoreFolders(string kind)
    {
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, "malformed.zip");
        List<SnapshotFile> files = kind switch
        {
            "missing-inventory" => null!,
            "null-file" => [null!],
            "bad-checksum" => [new("world.ark", 0, "invalid")],
            _ => [new("world.ark", -1, new string('a', 64))]
        };
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write(System.Text.Json.JsonSerializer.Serialize(new SnapshotManifest(id, ArkEdition.ASE, 1, DateTimeOffset.UtcNow, files)));
        }
        var snapshots = new SnapshotArchive();
        await Assert.ThrowsAsync<InvalidDataException>(() => snapshots.VerifyAsync(archive));
        var parent = Path.Combine(root, "not-created");
        await Assert.ThrowsAsync<InvalidDataException>(() => snapshots.ExtractToNewDirectoryAsync(archive,
            Path.Combine(parent, "restore"), id, ArkEdition.ASE));
        Assert.False(Directory.Exists(parent));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
