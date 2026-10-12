using System.Diagnostics;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class IdleManagerShutdownHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualHostClosesOnlyWhenRegisteredInstallationsCanBeVerifiedIdle(bool unverifiedServer)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-idle-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catalog = new CatalogDocument(1, [], unverifiedServer
            ? [new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "missing"),
                new() { Name = "Unverified", GamePort = 7777, QueryPort = 27015, RconPort = 32330 })] : []);
        await new AtomicJsonStore<CatalogDocument>(Path.Combine(root, "catalog.json")).SaveAsync(catalog);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "host-fixture", "ArkServerManager.Host.dll"));
        start.ArgumentList.Add("--data-root"); start.ArgumentList.Add(root);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            var client = new LocalManagerClient(root);
            while (true)
            {
                try { await client.CallAsync<System.Text.Json.JsonElement>("ping", null, TimeSpan.FromMilliseconds(500), deadline.Token); break; }
                catch (TimeoutException) { Assert.False(process.HasExited); }
            }
            var response = await client.CallAsync<IdleManagerShutdownResult>("app-update-idle-shutdown", null, TimeSpan.FromSeconds(5), deadline.Token);
            Assert.Equal(!unverifiedServer, response.Accepted);
            if (unverifiedServer)
            {
                Assert.False(process.HasExited);
                await client.CallAsync<System.Text.Json.JsonElement>("ping", null, TimeSpan.FromSeconds(2), deadline.Token);
            }
            else
            {
                await process.WaitForExitAsync(deadline.Token);
                Assert.True(process.ExitCode == 0, await error);
                using var ownership = new FileStream(Path.Combine(root, "manager-host.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await output; await error;
            Directory.Delete(root, true);
        }
    }
}
