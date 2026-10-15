using System.Text.Json;
using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class WindowsFirewallManagerTests
{
    [Theory]
    [InlineData(false, 0, ServerNetworkScope.LocalNetwork)]
    [InlineData(false, 0, ServerNetworkScope.Internet)]
    [InlineData(false, 1, ServerNetworkScope.LocalNetwork)]
    [InlineData(false, 1, ServerNetworkScope.Internet)]
    [InlineData(true, 0, ServerNetworkScope.LocalNetwork)]
    public async Task SetupChecksCurrentPortsAndReportsExecutorFailure(bool stale, int exitCode, ServerNetworkScope scope)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-firewall-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var exe = Path.Combine(root, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!); await File.WriteAllTextAsync(exe, "fixture");
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root,
                new() { Name = "Test", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
            await repository.ChangeAsync(catalog => catalog.AddServer(server));
            var lifecycle = new ServerLifecycleCoordinator(repository, _ => throw new InvalidOperationException(), (_, _) => Task.CompletedTask);
            await using var jobs = new BackgroundOperations(8, 1);
            var executions = 0;
            var manager = new WindowsFirewallManagerOperations(repository, jobs, lifecycle, (script, _) =>
            {
                executions++;
                if (script.Contains("New-NetFirewallRule"))
                {
                    Assert.Contains("-LocalPort 7777,7778,27015", script);
                    Assert.Contains("-RemoteAddress "+(scope==ServerNetworkScope.LocalNetwork?"'LocalSubnet'":"'Any'"),script);
                }
                else { Assert.Contains("Remove-NetFirewallRule", script); Assert.Contains("Get-NetFirewallApplicationFilter", script); Assert.Contains("'ARK Server Manager'", script); }
                return Task.FromResult(exitCode);
            }, managementRoot: root);
            var selection = new FirewallSelection(server.Id, root, server.Edition, stale ? 7779 : 7777, 27015, scope, true);
            var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "server-firewall-setup-start", JsonSerializer.SerializeToElement(selection));
            manager.Start(request); manager.Start(request);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (jobs.Find(request.Id)!.FinishedAt is null) await Task.Delay(10, deadline.Token);
            Assert.Equal(!stale && exitCode == 0 ? BackgroundOperationState.Succeeded : BackgroundOperationState.Failed, jobs.Find(request.Id)!.State);
            Assert.Equal(stale ? 0 : 1, executions);
            var receipt = await manager.ReadRecordedAsync(server.Id);
            if (stale) Assert.Null(receipt.Record);
            else
            {
                Assert.Equal(request.Id, receipt.Record!.OperationId); Assert.Equal(selection.Scope, receipt.Record.Scope);
                Assert.Equal(exitCode == 0, receipt.Record.Completed);
                var fresh = new WindowsFirewallManagerOperations(repository, jobs, lifecycle, (_, _) => throw new InvalidOperationException("Reading must not elevate or apply."), root);
                Assert.Equal(receipt, await fresh.ReadRecordedAsync(server.Id));
                Assert.Equal(1, executions);
            }
            if (!stale && exitCode == 0)
            {
                await manager.RemoveForDeletionAsync(server, CancellationToken.None);
                await manager.RemoveForDeletionAsync(server, CancellationToken.None);
                Assert.Equal(2, executions);
                var record = await new AtomicJsonStore<FirewallSetupRecord>(Path.Combine(root, "app-settings", "firewall-" + server.Id.ToString("N") + ".json")).LoadAsync();
                Assert.True(record!.Removed);
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
