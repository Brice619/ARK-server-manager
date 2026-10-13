using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class SharedServerFilesTests
{
    [Fact]
    public async Task LongArkAssetPathsAndTemporaryLinkNamesShareWithoutAnotherDownload()
    {
        var root = TemporaryRoot();
        try
        {
            var source = Path.Combine(root, "servers", Guid.NewGuid().ToString("N"));
            var target = Path.Combine(root, "servers", Guid.NewGuid().ToString("N"));
            await PackageAsync(source, "original", default);
            var relative = Path.Combine("ShooterGame", "Content", new string('a', 80), new string('b', 80), new string('c', 100) + ".uasset");
            var longAsset = Path.Combine(source, relative);
            Assert.True(longAsset.Length > 260);
            Directory.CreateDirectory(Path.GetDirectoryName(longAsset)!);
            await File.WriteAllTextAsync(longAsset, "long asset path");
            var store = new SharedServerFiles(root, ArkEdition.ASE, ServerPlatform.Windows);
            await store.RegisterAsync(source);
            Assert.True(await store.ReuseAsync(target));
            Assert.True(NativeGameFileLinks.SameFile(longAsset, Path.Combine(target, relative)));
            Assert.Equal(3u, NativeGameFileLinks.Count(longAsset));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task FilesDownloadedByPreviousInstallerAreAdoptedWithoutAnotherDownload()
    {
        var root = TemporaryRoot();
        try
        {
            var existingId = Guid.NewGuid();
            var existing = Path.Combine(root, "servers", ".ark-create-" + existingId.ToString("N"));
            await PackageAsync(existing, "already installed", default);
            await new AtomicJsonStore<ServerInstallationRecord>(Path.Combine(root, "app-settings", "creation", existingId.ToString("N") + ".json"))
                .SaveAsync(new(1, existingId, ArkEdition.ASE, ServerPlatform.Windows, existing,
                    Path.Combine(root, "servers", existingId.ToString("N")), ServerInstallationPhase.FilesReady));
            var service = new ServerInstallationPreparation(new(Path.Combine(root, "catalog.json")),
                (_, _, _) => throw new InvalidOperationException("Previously installed files must be reused"));
            var next = await service.PrepareAsync(root, Guid.NewGuid(), ArkEdition.ASE, ServerPlatform.Windows, true);
            Assert.Equal(ServerInstallationPhase.FilesReady, next.Phase);
            Assert.Equal("already installed", await File.ReadAllTextAsync(Asset(next.StagingDirectory)));
            Assert.True(NativeGameFileLinks.SameFile(Asset(existing), Asset(next.StagingDirectory)));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task MultipleServersDownloadOnceAndSharePhysicalGameFilesWhileStateAndModsRemainPrivate()
    {
        var root = TemporaryRoot();
        try
        {
            var calls = 0;
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            async Task<SteamInstallResult> Install(SteamInstallRequest request, IProgress<IReadOnlyList<ProcessLine>>? _, CancellationToken ct)
            {
                calls++;
                await PackageAsync(request.Directory, "original", ct);
                return new(376030, request.Directory, 0, true, "fixture package");
            }
            var first = await new ServerInstallationPreparation(repository, Install).PrepareAsync(root, Guid.NewGuid(), ArkEdition.ASE, ServerPlatform.Windows, true);
            var saved = Path.Combine(first.StagingDirectory, "ShooterGame", "Saved", "Config", "WindowsServer", "Game.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            await File.WriteAllTextAsync(saved, "private configuration");
            var mod = Path.Combine(first.StagingDirectory, "ShooterGame", "Content", "Mods", "123456", "private.uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(mod)!); await File.WriteAllTextAsync(mod, "private mod");
            // A fresh preparation object must reuse the persisted package after an app restart.
            var second = await new ServerInstallationPreparation(repository, Install).PrepareAsync(root, Guid.NewGuid(), ArkEdition.ASE, ServerPlatform.Windows, true);
            Assert.Equal(1, calls);
            var asset = Asset(first.StagingDirectory);
            Assert.Equal(3u, NativeGameFileLinks.Count(asset)); // pool + first server + second server
            Assert.Equal(3u, NativeGameFileLinks.Count(Asset(second.StagingDirectory)));
            Assert.False(File.Exists(Path.Combine(second.StagingDirectory, "ShooterGame", "Saved", "Config", "WindowsServer", "Game.ini")));
            Assert.False(File.Exists(Path.Combine(second.StagingDirectory, "ShooterGame", "Content", "Mods", "123456", "private.uasset")));
            var defaultConfig = Path.Combine(first.StagingDirectory, "Engine", "Config", "Base.ini");
            Assert.Equal(1u, NativeGameFileLinks.Count(defaultConfig));
            await File.WriteAllTextAsync(defaultConfig, "custom local default");
            Assert.Equal("default config", await File.ReadAllTextAsync(Path.Combine(second.StagingDirectory, "Engine", "Config", "Base.ini")));
            Directory.Delete(first.StagingDirectory, true);
            Assert.Equal("original", await File.ReadAllTextAsync(Asset(second.StagingDirectory)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UpdateDetachesFilesBeforeWritingAndOtherServerReceivesUpdatedSharedFilesOnlyAtRestart()
    {
        var root = TemporaryRoot();
        try
        {
            var first = Path.Combine(root, "servers", Guid.NewGuid().ToString("N"));
            var second = Path.Combine(root, "servers", Guid.NewGuid().ToString("N"));
            await PackageAsync(first, "original", default);
            var store = new SharedServerFiles(root, ArkEdition.ASE, ServerPlatform.Windows);
            await store.RegisterAsync(first);
            Assert.True(await store.ReuseAsync(second));
            var secondSave = Path.Combine(second, "ShooterGame", "Saved", "SavedArks", "TheIsland.ark");
            Directory.CreateDirectory(Path.GetDirectoryName(secondSave)!); await File.WriteAllTextAsync(secondSave, "world state");
            await SharedServerFiles.DetachBeforeWriteAsync(first, default);
            Assert.Equal(1u, NativeGameFileLinks.Count(Asset(first)));
            await File.WriteAllTextAsync(Asset(first), "updated");
            Assert.Equal("original", await File.ReadAllTextAsync(Asset(second)));
            await SharedServerFiles.RefreshAfterWriteAsync(new(ArkEdition.ASE, ServerPlatform.Windows, first, false), default);
            Assert.Equal("original", await File.ReadAllTextAsync(Asset(second)));
            Assert.True(await store.ReuseAsync(second));
            Assert.Equal("updated", await File.ReadAllTextAsync(Asset(second)));
            Assert.Equal(3u, NativeGameFileLinks.Count(Asset(second)));
            Assert.Equal("world state", await File.ReadAllTextAsync(secondSave));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingOrDamagedSharedFilesFailWithoutSilentlyDownloadingAnotherPackage()
    {
        var root = TemporaryRoot();
        try
        {
            var source = Path.Combine(root, "servers", Guid.NewGuid().ToString("N"));
            await PackageAsync(source, "original", default);
            var store = new SharedServerFiles(root, ArkEdition.ASE, ServerPlatform.Windows);
            await store.RegisterAsync(source);
            var hashes = Directory.GetFiles(Path.Combine(store.PoolDirectory, "files"));
            File.Delete(hashes[0]);
            await Assert.ThrowsAsync<IOException>(() => store.ReuseAsync(Path.Combine(root, "servers", Guid.NewGuid().ToString("N"))));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SharedFilesCannotBeAppliedOutsideOwnedServerFolders()
    {
        var root = TemporaryRoot();
        try
        {
            var source = Path.Combine(root, "servers", Guid.NewGuid().ToString("N"));
            await PackageAsync(source, "original", default);
            var store = new SharedServerFiles(root, ArkEdition.ASE, ServerPlatform.Windows);
            await store.RegisterAsync(source);
            await Assert.ThrowsAsync<IOException>(() => store.ReuseAsync(Path.Combine(root, "personal-files")));
            Assert.False(Directory.Exists(Path.Combine(root, "personal-files")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-shared-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static string Asset(string root) => Path.Combine(root, "ShooterGame", "Content", "Maps", "TheIsland.umap");
    private static async Task PackageAsync(string root, string content, CancellationToken ct)
    {
        var exe = Path.Combine(root, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!); await File.WriteAllTextAsync(exe, "fixture executable", ct);
        var asset = Asset(root); Directory.CreateDirectory(Path.GetDirectoryName(asset)!); await File.WriteAllTextAsync(asset, content, ct);
        var config = Path.Combine(root, "Engine", "Config", "Base.ini"); Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        await File.WriteAllTextAsync(config, "default config", ct);
    }
}
