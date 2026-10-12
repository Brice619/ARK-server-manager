using System.Net;
using System.Net.Sockets;
using System.Text;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class RegisteredRuntimeFactoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-runtime-factory-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UsesActualCredentialsAndOwnsProcessThroughReadinessAndStop(bool rejectPassword, bool rejectInstalledMods)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var binary = Path.Combine(root, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        await File.WriteAllTextAsync(binary, "read-only recognition fixture, not executed");
        var config = Path.Combine(root, "ShooterGame", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(config);
        const string password = "runtime-fixture-password";
        await File.WriteAllTextAsync(Path.Combine(config, "GameUserSettings.ini"),
            $"[ServerSettings]\nRCONEnabled=True\nRCONPort={port}\nServerAdminPassword={password}\n");
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var definition = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root,
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = port });
        await repository.ChangeAsync(c => c.AddServer(definition));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var network = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = peer.GetStream();
            var auth = await RconProtocol.ReadAsync(stream, timeout.Token);
            Assert.Equal(password, Encoding.UTF8.GetString(auth.Body));
            await stream.WriteAsync(RconProtocol.Encode(rejectPassword ? -1 : auth.Id, 2, ""), timeout.Token);
            if (rejectPassword) return;
            var command = await RconProtocol.ReadAsync(stream, timeout.Token);
            Assert.Equal("ListPlayers", Encoding.UTF8.GetString(command.Body));
            await stream.WriteAsync(RconProtocol.Encode(command.Id, 0, "No Players Connected"), timeout.Token);
        }, timeout.Token);
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? (OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe") : "/usr/bin/dotnet");
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixture", "ArkServerManager.ProcessFixture.dll");
        var stopped = false;
        var incidents = new List<ServerIncident>();
        var launchPrepared = false;
        var observed = new List<ServerState>();
        var checkedInstalledMods = false;
        var factory = new RegisteredRconRuntimeFactory(repository,
            async (selected, ct) =>
            {
                var current = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == selected.Id);
                Assert.Equal(definition.Id, current.Id);
                await Task.Delay(10, ct);
                launchPrepared = true;
                return new ProcessLaunch(dotnet, root, [fixture, "--wait", password], Path.Combine(root, "console.log"));
            },
            async (process, endpoint, ct) =>
            {
                Assert.Equal(password, endpoint.Password);
                stopped = true;
                await process.SendStandardInputAsync("stop", ct);
            }, (_, incident) => { incidents.Add(incident); return Task.CompletedTask; },
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(3), RconResponseMode.SinglePacket, (serverId, clusterId, state) =>
            {
                Assert.Equal(definition.Id, serverId); Assert.Null(clusterId); observed.Add(state);
            }, verifyReady: (selected, token) =>
            {
                checkedInstalledMods = true;
                Assert.Equal(definition.Id, selected.Id);
                if (rejectInstalledMods) throw new IOException("Installed mod assets are missing.");
                return Task.CompletedTask;
            });
        await using var runtime = factory.Create(definition.Id);
        if (rejectPassword || rejectInstalledMods)
        {
            if (rejectPassword) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runtime.StartAsync(definition, timeout.Token));
            else await Assert.ThrowsAsync<IOException>(() => runtime.StartAsync(definition, timeout.Token));
            Assert.Equal(ServerState.Failed, runtime.State);
            Assert.False(stopped);
            Assert.Contains(incidents, i => i.Stage == "startup");
        }
        else
        {
            await runtime.StartAsync(definition, timeout.Token);
            Assert.Equal(ServerState.Running, runtime.State);
            Assert.True(await runtime.StopAsync(timeout.Token));
            Assert.Equal(ServerState.Stopped, runtime.State);
            Assert.True(stopped);
        }
        await network.WaitAsync(timeout.Token);
        Assert.True(launchPrepared);
        Assert.Contains(ServerState.Starting, observed);
        Assert.Contains(rejectPassword || rejectInstalledMods ? ServerState.Failed : ServerState.Running, observed);
        Assert.Equal(!rejectPassword, checkedInstalledMods);
        if (rejectInstalledMods) Assert.DoesNotContain(ServerState.Running, observed);
        if (!rejectPassword && !rejectInstalledMods) Assert.Contains(ServerState.Stopped, observed);
        Assert.DoesNotContain(password, await File.ReadAllTextAsync(Path.Combine(root, "console.log"), timeout.Token));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
