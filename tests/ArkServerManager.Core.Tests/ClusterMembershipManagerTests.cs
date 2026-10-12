using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;
public sealed class ClusterMembershipManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcludedDetachAndMoveAreRefusedBeforeQueuingOrCatalogEffects(bool move)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-membership-scope-" + Guid.NewGuid().ToString("N"));
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        await using var jobs = new BackgroundOperations(8, 1);
        var lifecycle = new ServerLifecycleCoordinator(repository, _ => throw new InvalidOperationException("Must not launch."), (_, _) => Task.CompletedTask);
        var guard = new InstallationStopGuard(_ => throw new InvalidOperationException("Must not inspect excluded operation."));
        var manager = new ClusterMembershipManagerOperations(repository, jobs, lifecycle, guard, Joining(root, repository));
        var edit = new ClusterMembershipEdit([new(Guid.NewGuid(), ArkEdition.ASE, Path.Combine(root, "server"), Guid.NewGuid())],
            move ? Guid.NewGuid() : null, true);
        var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "cluster-membership-save-start", JsonSerializer.SerializeToElement(edit));
        Assert.Throws<ArgumentException>(() => manager.Start(request));
        Assert.Null(jobs.Find(request.Id));
        Assert.False(Directory.Exists(root));
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task CombiningIsAtomicAndReplaysOriginalRequest(bool completeScan, bool stale)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-membership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var servers = new List<ServerDefinition>();
            for (var index = 0; index < 2; index++)
            {
                var folder = Path.Combine(root, index.ToString());
                var exe = Path.Combine(folder, "ShooterGame", "Binaries", OperatingSystem.IsWindows() ? "Win64" : "Linux",
                    OperatingSystem.IsWindows() ? "ShooterGameServer.exe" : "ShooterGameServer");
                Directory.CreateDirectory(Path.GetDirectoryName(exe)!); await File.WriteAllTextAsync(exe, "fixture");
                var config = Path.Combine(folder, "ShooterGame", "Saved", "Config", OperatingSystem.IsWindows() ? "WindowsServer" : "LinuxServer");
                Directory.CreateDirectory(config);
                await File.WriteAllTextAsync(Path.Combine(config, "GameUserSettings.ini"), "[ServerSettings]\nTamingSpeedMultiplier=2\n");
                servers.Add(new(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", folder, new()
                { Name = "Personal " + index, GamePort = 7777 + index * 10, QueryPort = 27015 + index * 10, RconPort = 32330 + index * 10 }));
            }
            await repository.ChangeAsync(catalog => { foreach (var server in servers) catalog.AddServer(server); });
            await using var jobs = new BackgroundOperations(8, 1);
            var lifecycle = new ServerLifecycleCoordinator(repository, _ => throw new InvalidOperationException("Must not start servers."), (_, _) => Task.CompletedTask);
            var guard = new InstallationStopGuard(_ => Task.FromResult(new ServerProcessScan(DateTimeOffset.UtcNow, [],
                completeScan ? [] : [new(1234, "unknown", "Cannot inspect process")], [])));
            var manager = new ClusterMembershipManagerOperations(repository, jobs, lifecycle, guard, Joining(root, repository));
            var review = (await manager.ReadAsync()).ToList();
            if (stale) review[1] = review[1] with { Directory = review[1].Directory + "-changed" };
            var target = Guid.NewGuid();
            var edit = new ClusterMembershipEdit(review, target, true) { NewClusterName = "Combined" };
            var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "cluster-membership-save-start", JsonSerializer.SerializeToElement(edit));
            manager.Start(request); manager.Start(request);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (jobs.Find(request.Id)!.FinishedAt is null) await Task.Delay(10, deadline.Token);
            var result = jobs.Find(request.Id)!;
            var actual = await repository.ReadAsync();
            if (completeScan && !stale)
            {
                Assert.Equal(BackgroundOperationState.Succeeded, result.State);
                Assert.Single(actual.Clusters); Assert.All(actual.Servers, server => Assert.Equal(target, server.ClusterId));
                foreach (var server in actual.Servers) Assert.Equal(servers.Single(original => original.Id == server.Id).Settings.Name, server.Settings.Name);
            }
            else
            {
                Assert.Equal(BackgroundOperationState.Failed, result.State);
                Assert.Empty(actual.Clusters); Assert.All(actual.Servers, server => Assert.Null(server.ClusterId));
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static ClusterJoiningTransaction Joining(string root, CatalogRepository repository) => new(repository, new(root),
        new(repository, new PresetLibrary(Path.Combine(root, "presets.json"))),
        new PresetApplicationQueue(repository, Path.Combine(root, "pending-presets.json"), (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask),
        (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
}
