using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerUpdateManagerOperationsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-update-manager-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData(376030u, true)]
    [InlineData(2430930u, false)]
    public async Task UpdateCheckJobsVerifyGameAndDeduplicateReconnect(uint appId, bool valid)
    {
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json")); var id = Guid.NewGuid();
        await repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", Path.Combine(root, "server"), new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 })));
        await using var jobs = new BackgroundOperations(8, 1); var applied = 0; var queries = 0;
        var manager = new ServerUpdateManagerOperations(repository, jobs,
            (_, _) => { queries++; return Task.FromResult(new SteamBuild(appId, 2, "Fixture", "windows")); },
            (server, build, _) => { applied++; return Task.FromResult(new ServerUpdateCheckResult(server.Id, 1, build.BuildId,
                new(false, true, false, "Queued"))); });
        var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "server-update-check-start",
            JsonSerializer.SerializeToElement(new ServerUpdateCheckRequest(id)));
        var submitted = manager.Start(request); Assert.Equal(submitted.Id, manager.Start(request).Id);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (jobs.Find(request.Id)!.FinishedAt is null) await Task.Delay(5, deadline.Token);
        var completed = jobs.Find(request.Id)!;
        Assert.Equal(valid ? BackgroundOperationState.Succeeded : BackgroundOperationState.Failed, completed.State);
        Assert.Equal(valid ? 1 : 0, applied); Assert.Equal(1, queries);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
