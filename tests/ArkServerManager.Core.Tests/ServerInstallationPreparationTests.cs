using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerInstallationPreparationTests
{
    [Fact]
    public async Task InventoryReportsMalformedRecordsWithoutCreatingOrChangingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-install-inventory-" + Guid.NewGuid().ToString("N"));
        Assert.Empty((await ServerInstallationPreparation.ReadInventoryAsync(root)).Records);
        Assert.False(Directory.Exists(root));
        var directory = Path.Combine(root, "app-settings", "creation");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(path, "invalid fixture");
            var inventory = await ServerInstallationPreparation.ReadInventoryAsync(root);
            Assert.Empty(inventory.Records);
            Assert.Equal(1, inventory.UnreadableRecords);
            Assert.Equal("invalid fixture", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResumeReusesOnlyRecordedPartialDownloadAfterStoppedAuthorization(bool authorize)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var id = Guid.NewGuid(); var calls = 0;
            var service = new ServerInstallationPreparation(new(Path.Combine(root, "catalog.json")), async (request, _, ct) =>
            {
                calls++;
                var partial = Path.Combine(request.Directory, "partial.download");
                if (calls == 1) { await File.WriteAllTextAsync(partial, "retained bytes", ct); throw new IOException("fixture interruption"); }
                Assert.Equal("retained bytes", await File.ReadAllTextAsync(partial, ct));
                var exe = Path.Combine(request.Directory, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
                await File.WriteAllTextAsync(exe, "fixture executable", ct);
                return new SteamInstallResult(376030, request.Directory, 0, true, "fixture");
            });
            await Assert.ThrowsAsync<IOException>(() => service.PrepareAsync(root, id, ArkEdition.ASE, ServerPlatform.Windows, true));
            var work = service.ResumeAsync(root, id, true, (_, change, ct) => authorize ? change(ct) : Task.CompletedTask);
            if (authorize)
            {
                Assert.Equal(ServerInstallationPhase.FilesReady, (await work).Phase);
                Assert.Equal(ServerInstallationPhase.FilesReady, (await service.ResumeAsync(root, id, true, (_, change, ct) => change(ct))).Phase);
            }
            else await Assert.ThrowsAsync<InvalidOperationException>(() => work);
            Assert.Equal(authorize ? 2 : 1, calls);
            Assert.Equal("retained bytes", await File.ReadAllTextAsync(Path.Combine(root, "servers", ".ark-create-" + id.ToString("N"), "partial.download")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordsOnlyVerifiedFilesAndRetainsIncompleteDownloads(bool hasExecutable)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-prepare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var id = Guid.NewGuid(); var calls = 0;
            var service = new ServerInstallationPreparation(repository, async (request, _, ct) =>
            {
                calls++;
                Assert.True(request.ValidateFiles);
                await File.WriteAllTextAsync(Path.Combine(request.Directory, "partial.download"), "download fixture", ct);
                if (hasExecutable)
                {
                    var exe = Path.Combine(request.Directory, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
                    Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
                    await File.WriteAllTextAsync(exe, "executable fixture", ct);
                }
                return new SteamInstallResult(376030, request.Directory, 0, true, "fixture confirmation");
            });
            var work = service.PrepareAsync(root, id, ArkEdition.ASE, ServerPlatform.Windows, true);
            if (hasExecutable) Assert.Equal(ServerInstallationPhase.FilesReady, (await work).Phase);
            else await Assert.ThrowsAsync<IOException>(() => work);
            var record = await new AtomicJsonStore<ServerInstallationRecord>(Path.Combine(root, "app-settings", "creation", id.ToString("N") + ".json")).LoadAsync();
            Assert.Equal(hasExecutable ? ServerInstallationPhase.FilesReady : ServerInstallationPhase.Failed, record!.Phase);
            Assert.True(File.Exists(Path.Combine(record.StagingDirectory, "partial.download")));
            Assert.False(Directory.Exists(record.FinalDirectory));
            Assert.Empty((await repository.ReadAsync()).Servers);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(root, id, ArkEdition.ASE, ServerPlatform.Windows, true));
            Assert.Equal(1, calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingConsentCannotCreateFoldersOrInvokeInstaller()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-no-consent-" + Guid.NewGuid().ToString("N"));
        var service = new ServerInstallationPreparation(new(Path.Combine(root, "catalog.json")),
            (_, _, _) => throw new InvalidOperationException("Installer must not run"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(root, Guid.NewGuid(), ArkEdition.ASE, ServerPlatform.Windows, false));
        Assert.False(Directory.Exists(root));
    }
}
