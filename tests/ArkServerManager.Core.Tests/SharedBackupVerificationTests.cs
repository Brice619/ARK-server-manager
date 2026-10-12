using ArkServerManager.Core;
using System.IO.Compression;

namespace ArkServerManager.Core.Tests;

public sealed class SharedBackupVerificationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-shared-verify-" + Guid.NewGuid().ToString("N"));
    private readonly Guid clusterId = Guid.NewGuid();
    private CatalogRepository Repository => new(Path.Combine(root, "catalog.json"));

    private async Task<string> SeedAsync(string variant = "valid")
    {
        var catalog = await Repository.ChangeAsync(c => c.AddCluster(new(clusterId, "Fixture", ArkEdition.ASE, new())));
        var storage = ManagedClusterStorage.Prepare(catalog, clusterId, root);
        await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player.transfer"), "player data");
        var path = Path.Combine(storage.Backups, "hour-00000001.zip");
        var manifest = await new SnapshotArchive().CreateAsync(storage.Transfers, path,
            variant == "owner" ? Guid.NewGuid() : clusterId,
            variant == "edition" ? ArkEdition.ASA : ArkEdition.ASE, variant == "hour" ? 2 : 1,
            scope: variant == "scope" ? SnapshotScope.Server : SnapshotScope.SharedCluster,
            capture: new(Guid.NewGuid(), DateTimeOffset.UtcNow));
        await new AtomicJsonStore<AutomaticBackupState>(Path.Combine(storage.Backups, "backup-history.json")).SaveAsync(
            new(1, clusterId, ArkEdition.ASE, TimeSpan.FromHours(1).Ticks, null, null,
                [new(1, "hour-00000001.zip", manifest.CreatedAt)], null) { Scope = SnapshotScope.SharedCluster });
        return path;
    }

    [Fact]
    public async Task RecordedSharedArchiveVerifiesWithoutChangingTransfersOrArchive()
    {
        var path = await SeedAsync(); var before = await File.ReadAllBytesAsync(path);
        var result = await BackupVerification.VerifySharedAsync(Repository, root, new(clusterId, 1));
        Assert.Equal(clusterId, result.ClusterId); Assert.Equal(1, result.RuntimeHour);
        Assert.Equal(1, result.FileCount); Assert.Equal(11, result.TotalBytes);
        Assert.NotNull(result.Capture); Assert.NotEqual(Guid.Empty, result.Capture.Id);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        var storage = ManagedClusterStorage.Resolve(await Repository.ReadAsync(), clusterId, root);
        Assert.Equal("player data", await File.ReadAllTextAsync(Path.Combine(storage.Transfers, "player.transfer")));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("edition")]
    [InlineData("hour")]
    [InlineData("scope")]
    public async Task ValidArchiveForDifferentIdentityCannotVerify(string variant)
    {
        var path = await SeedAsync(variant); var before = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupVerification.VerifySharedAsync(Repository, root, new(clusterId, 1)));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task UnrecordedPointAndCorruptedTransferAreRejected()
    {
        var path = await SeedAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => BackupVerification.VerifySharedAsync(Repository, root, new(clusterId, 2)));
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("state/player.transfer")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("state/player.transfer").Open());
            writer.Write("changed player data");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupVerification.VerifySharedAsync(Repository, root, new(clusterId, 1)));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
