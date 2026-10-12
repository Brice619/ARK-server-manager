using ArkServerManager.Core;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

internal static class NativeClusterSmoke
{
    public static async Task<object> RunAsync(string managementRoot, string evidence, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(managementRoot) || !Path.IsPathFullyQualified(evidence))
            throw new ArgumentException("Choose absolute Windows verification paths.");
        if (!File.Exists(Path.Combine(managementRoot, "copies-ready.json")) || Directory.Exists(evidence) || File.Exists(evidence))
            throw new IOException("Use fresh prepared cluster copies and a new evidence folder.");
        var directories = new[] { Path.Combine(managementRoot, "Island"), Path.Combine(managementRoot, "Scorched") };
        foreach (var directory in directories)
        {
            if ((await new InstallationStopGuard().CheckAsync(directory, ct)).State != InstallationStopState.Stopped)
                throw new IOException("A cluster verification installation is not stopped.");
            if (Directory.Exists(Path.Combine(directory, "ShooterGame", "Saved")))
                throw new IOException("Cluster verification requires fresh copies without saved state.");
        }
        Directory.CreateDirectory(evidence);
        var clusterId = Guid.NewGuid();
        var join = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var admins = new Dictionary<Guid, string>();
        var definitions = new List<ServerDefinition>();
        var occupied = new HashSet<int>();
        for (var index = 0; index < 2; index++)
        {
            var game = AvailablePorts(occupied);
            var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, index == 0 ? "TheIsland" : "ScorchedEarth_P", directories[index],
                new() { Name = "ARK Cluster Verification " + index, MaxPlayers = 2, GamePort = game, QueryPort = game + 2, RconPort = game + 3 }, clusterId);
            definitions.Add(server); admins[server.Id] = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var generated = GuidedServerConfiguration.Build(new() { Server = server, PvE = true,
                AdministratorPassword = admins[server.Id], JoinPassword = join });
            var config = Path.Combine(server.Directory, "ShooterGame", "Saved", "Config", "WindowsServer");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "GameUserSettings.ini"), generated.UserSettings, ct);
            await File.WriteAllTextAsync(Path.Combine(config, "Game.ini"), generated.GameSettings, ct);
        }
        var repository = new CatalogRepository(Path.Combine(evidence, "catalog.json"));
        await repository.ChangeAsync(c =>
        {
            c.AddCluster(new(clusterId, "Native ASE verification", ArkEdition.ASE, definitions[0].Settings));
            foreach (var server in definitions) c.AddServer(server);
        }, ct);
        var storage = ManagedClusterStorage.Prepare(await repository.ReadAsync(ct), clusterId, managementRoot);
        var marker = Path.Combine(storage.Transfers, ".verification-marker.txt");
        await File.WriteAllTextAsync(marker, "shared state remains", ct);
        var activity = new ActivityJournal(Path.Combine(evidence, "activity.jsonl"));
        var runtimes = new Dictionary<Guid, SupervisedServerRuntime>();
        var plans = new Dictionary<Guid, ProcessLaunch>();
        using var gate = new SemaphoreSlim(1, 1);
        var factory = new RegisteredRconRuntimeFactory(repository, async (server, token) =>
        {
            var launch = NativeServerLaunchPlan.BuildManaged(await repository.ReadAsync(token), server.Id, managementRoot,
                ServerPlatform.Windows, true, Path.Combine(evidence, server.Map + "-console.log"));
            plans[server.Id] = launch;
            return launch with { Arguments = [.. launch.Arguments, "-MULTIHOME=127.0.0.1"], Secrets = [admins[server.Id], join] };
        }, (process, endpoint, token) => new ArkGracefulShutdown().RequestWithResponseConfirmationAsync(process, endpoint,
            response => response.Trim().Equals("World Saved", StringComparison.OrdinalIgnoreCase), token),
            (id, incident) => activity.RecordIncidentAsync(id, incident), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(20),
            TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2), RconResponseMode.SinglePacket,
            (id, _, state) => Console.WriteLine(definitions.Single(s => s.Id == id).Map + ": " + state), gate);
        var coordinator = new ServerLifecycleCoordinator(repository, id =>
        {
            var runtime = factory.Create(id); runtimes.Add(id, runtime); return runtime;
        }, (_, _) => Task.CompletedTask, gate, (server, token) => new InstallationStopGuard().CheckAsync(server.Directory, token));
        var started = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new { StartedAt = started, Completed = false }), ct);
        try
        {
            var start = await coordinator.ExecuteAsync(definitions[0].Id, LifecycleAction.Start, ct);
            if (!start.Success) throw new IOException("A native cluster map did not reach administration readiness: " + string.Join("; ", start.Members.Select(m => m.Error)));
            var statuses = await coordinator.StatusAsync(ct);
            var bothReady = statuses.Count == 2 && statuses.All(s => s.Managed && s.State == ServerState.Running);
            if (!bothReady) throw new IOException("Both native cluster maps were not running together after startup.");
            var before = await new ServerProcessDiscovery().ScanAsync(ct);
            var sibling = before.Candidates.Single(p => p.Directory.Equals(definitions[1].Directory, StringComparison.OrdinalIgnoreCase));
            var stopFirst = await coordinator.ExecuteMemberAsync(definitions[0].Id, LifecycleAction.Stop, ct);
            var after = await new ServerProcessDiscovery().ScanAsync(ct);
            var siblingContinued = after.Candidates.Any(p => p.ProcessId == sibling.ProcessId && p.StartedAt == sibling.StartedAt)
                && runtimes[definitions[1].Id].State == ServerState.Running;
            var sharedPreserved = await File.ReadAllTextAsync(marker, ct) == "shared state remains";
            var stopAll = await coordinator.StopAllAsync(ct);
            var stopped = new List<bool>();
            foreach (var server in definitions) stopped.Add((await new InstallationStopGuard().CheckAsync(server.Directory, ct)).State == InstallationStopState.Stopped);
            var result = new { Edition = "ASE", Maps = definitions.Select(s => s.Map).ToArray(), BothAdministrationReady = bothReady,
                SelectedMapStopped = stopFirst.Success, SiblingProcessContinuedUnchanged = siblingContinued, SharedStatePreserved = sharedPreserved,
                SameClusterAndTransferArguments = plans.Values.All(p => p.Arguments.Contains("-clusterid=" + clusterId.ToString("N"))
                    && p.Arguments.Contains("-ClusterDirOverride=" + storage.Transfers)),
                GracefulShutdown = stopAll.Success, NativeStopped = stopped.All(value => value),
                WorldFiles = definitions.Select(s => Directory.GetFiles(Path.Combine(s.Directory, "ShooterGame", "Saved"), "*.ark", SearchOption.AllDirectories).Length).ToArray(),
                ElapsedSeconds = (DateTimeOffset.UtcNow - started).TotalSeconds, PlayerTransfersVerified = false, GameplayConnectionsVerified = false };
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), ct);
            return result;
        }
        finally { foreach (var runtime in runtimes.Values) await runtime.DisposeAsync(); }
    }

    private static int AvailablePorts(HashSet<int> occupied)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var first = RandomNumberGenerator.GetInt32(49152, 60000);
            if (Enumerable.Range(first, 4).Any(occupied.Contains)) continue;
            try
            {
                using var game = new UdpClient(new IPEndPoint(IPAddress.Any, first));
                using var adjacent = new UdpClient(new IPEndPoint(IPAddress.Any, first + 1));
                using var query = new UdpClient(new IPEndPoint(IPAddress.Any, first + 2));
                using var rcon = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                rcon.Bind(new IPEndPoint(IPAddress.Any, first + 3));
                foreach (var port in Enumerable.Range(first, 4)) occupied.Add(port);
                return first;
            }
            catch (SocketException) { }
        }
        throw new IOException("No cluster verification port set is available.");
    }
}
