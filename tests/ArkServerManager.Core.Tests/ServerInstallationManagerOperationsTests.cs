using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerInstallationManagerOperationsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JobRequiresConsentAndReusesAcceptedRequest(bool consent)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-install-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var jobs = new BackgroundOperations(8, 1);
            using var gate = new SemaphoreSlim(1, 1);
            var installs = 0; var factories = 0; var checks = 0;
            var preparation = new ServerInstallationPreparation(new(Path.Combine(root, "catalog.json")), async (request, _, ct) =>
            {
                installs++;
                var platform = request.Platform == ServerPlatform.Windows ? "Win64" : "Linux";
                var exe = Path.Combine(request.Directory, "ShooterGame", "Binaries", platform,
                    request.Platform == ServerPlatform.Windows ? "ShooterGameServer.exe" : "ShooterGameServer");
                Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
                await File.WriteAllTextAsync(exe, "fixture executable", ct);
                return new SteamInstallResult(376030, request.Directory, 0, true, "fixture");
            });
            var routing = new ServerInstallationManagerOperations(root, jobs, gate,
                _ => { factories++; return Task.FromResult(preparation); },
                (_, change, ct) => { checks++; return change(ct); });
            var payload = new ServerInstallationRequest(Guid.NewGuid(), ArkEdition.ASE,
                OperatingSystem.IsWindows() ? ServerPlatform.Windows : ServerPlatform.Linux, consent, false);
            var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "server-installation-start", JsonSerializer.SerializeToElement(payload));
            if (!consent)
            {
                Assert.Throws<ArgumentException>(() => routing.Start(request));
                Assert.Equal(0, factories); Assert.Equal(0, checks); Assert.Equal(0, installs);
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
                return;
            }
            var first = Assert.IsType<BackgroundOperation>(routing.Start(request));
            var repeated = Assert.IsType<BackgroundOperation>(routing.Start(request));
            Assert.Equal(first.CreatedAt, repeated.CreatedAt);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!jobs.Find(first.Id)!.FinishedAt.HasValue) await Task.Delay(10, deadline.Token);
            var finished = jobs.Find(first.Id)!;
            Assert.Equal(BackgroundOperationState.Succeeded, finished.State);
            Assert.Equal(ServerInstallationPhase.FilesReady, finished.Result!.Value.Deserialize<ServerInstallationRecord>()!.Phase);
            Assert.Equal(1, factories); Assert.Equal(1, checks); Assert.Equal(1, installs);
            var inventory = await routing.InventoryAsync();
            var entry = Assert.Single(inventory.Entries);
            Assert.Equal(request.Id, entry.Job!.Id);
            Assert.Equal(ServerInstallationPhase.FilesReady, entry.Record!.Phase);
            var restarted = new ServerInstallationManagerOperations(root, jobs, gate,
                _ => throw new InvalidOperationException("Inventory must not prepare tools"),
                (_, _, _) => throw new InvalidOperationException("Inventory must not authorize files"));
            var recorded = Assert.Single((await restarted.InventoryAsync()).Entries);
            Assert.Null(recorded.Job);
            Assert.Equal(ServerInstallationPhase.FilesReady, recorded.Record!.Phase);
        }
        finally { Directory.Delete(root, true); }
    }
}
