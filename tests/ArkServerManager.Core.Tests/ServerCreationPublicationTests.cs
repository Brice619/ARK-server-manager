using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerCreationPublicationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-create-publication-" + Guid.NewGuid().ToString("N"));
    private CatalogRepository Repository => new(Path.Combine(root, "app-settings", "catalog.json"));

    private async Task<(ServerDefinition Server, string Staging)> Prepare(Guid? cluster = null, string name = "Creation fixture", string map = "TheIsland")
    {
        var id = Guid.NewGuid();
        using var reservation = new PortAllocator().Reserve((await Repository.ReadAsync()).Servers.Select(s => s.Settings), 44000, 45000, 46000);
        var settings = new ServerSettings { Name = name, GamePort = reservation.GamePort, QueryPort = reservation.QueryPort, RconPort = reservation.RconPort };
        var staging = Path.Combine(root, "servers", ".ark-create-" + id.ToString("N"));
        var executable = Path.Combine(staging, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!); await File.WriteAllTextAsync(executable, "fixture executable only");
        var server = new ServerDefinition(id, ArkEdition.ASE, map, Path.Combine(root, "servers", id.ToString("N")), settings, cluster);
        await new AtomicJsonStore<ServerInstallationRecord>(Path.Combine(root, "app-settings", "creation", id.ToString("N") + ".json"))
            .SaveAsync(new(1, id, server.Edition, ServerPlatform.Windows, staging, server.Directory, ServerInstallationPhase.FilesReady));
        var configuration = GuidedServerConfiguration.Build(new() { Server = server, PvE = true, AdministratorPassword = "fixture-private-password" });
        await InitialServerConfiguration.WriteAsync(root, id,
            configuration.UserSettings, configuration.GameSettings, (_, action, ct) => action(ct));
        return (server, staging);
    }

    [Fact]
    public async Task PublishesAndRegistersClusterOnceAndReplaysWithoutSecretsInJournal()
    {
        var clusterId = Guid.NewGuid(); var (server, staging) = await Prepare(clusterId);
        var cluster = new ClusterDefinition(clusterId, "Fixture cluster", ArkEdition.ASE, server.Settings);
        var result = await ServerCreationPublication.ExecuteAsync(Repository, root, server, cluster, (_, action, ct) => action(ct));
        Assert.Equal(ServerCreationPublicationPhase.Registered, result.Phase);
        Assert.False(Directory.Exists(staging)); Assert.True(Directory.Exists(server.Directory));
        Assert.Equal(result, await ServerCreationPublication.ExecuteAsync(Repository, root, server, cluster, (_, action, ct) => action(ct)));
        var catalog = await Repository.ReadAsync(); Assert.Single(catalog.Servers); Assert.Single(catalog.Clusters);
        var journal = await File.ReadAllTextAsync(Path.Combine(root, "app-settings", "creation-publication", server.Id.ToString("N") + ".json"));
        Assert.DoesNotContain("fixture-private-password", journal);
    }

    [Fact]
    public async Task RecoversPublishedFolderBeforeRegistrationAndRejectsChangedSelection()
    {
        var (server, _) = await Prepare();
        var result = await ServerCreationPublication.ExecuteAsync(Repository, root, server, null, (_, action, ct) => action(ct));
        // Reconstruct the durable state of interruption after folder publication and before catalog commit.
        await Repository.ChangeAsync(c => c.RemoveServer(server.Id));
        await new AtomicJsonStore<ServerCreationPublicationRecord>(Path.Combine(root, "app-settings", "creation-publication", server.Id.ToString("N") + ".json"))
            .SaveAsync(result with { Phase = ServerCreationPublicationPhase.Published });
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerCreationPublication.ExecuteAsync(Repository, root,
            server with { Map = "Ragnarok" }, null, (_, action, ct) => action(ct)));
        Assert.Empty((await Repository.ReadAsync()).Servers);
        Assert.Equal(ServerCreationPublicationPhase.Registered,
            (await ServerCreationPublication.ExecuteAsync(Repository, root, server, null, (_, action, ct) => action(ct))).Phase);
        Assert.Single((await Repository.ReadAsync()).Servers);
    }

    [Fact]
    public async Task DeniedScopeDoesNotPublishOrRegister()
    {
        var (server, staging) = await Prepare();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerCreationPublication.ExecuteAsync(Repository, root, server, null, (_, _, _) => Task.CompletedTask));
        Assert.True(Directory.Exists(staging)); Assert.False(Directory.Exists(server.Directory));
        Assert.Empty((await Repository.ReadAsync()).Servers);
        Assert.False(Directory.Exists(Path.Combine(root, "app-settings", "creation-publication")));
    }

    [Fact]
    public async Task BusyReviewedPortLeavesStagingAndCatalogUntouched()
    {
        var (server, staging) = await Prepare();
        using var occupied = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { ExclusiveAddressUse = true };
        occupied.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, server.Settings.RconPort)); occupied.Listen(1);
        await Assert.ThrowsAsync<IOException>(() => ServerCreationPublication.ExecuteAsync(Repository, root, server, null, (_, action, ct) => action(ct)));
        Assert.True(Directory.Exists(staging)); Assert.False(Directory.Exists(server.Directory));
        Assert.Empty((await Repository.ReadAsync()).Servers);
        Assert.False(Directory.Exists(Path.Combine(root, "app-settings", "creation-publication")));
    }

    [Fact]
    public async Task ChangedPublishedConfigurationRefusesRecoveryWithoutRegistration()
    {
        var (server, _) = await Prepare();
        var result = await ServerCreationPublication.ExecuteAsync(Repository, root, server, null, (_, action, ct) => action(ct));
        await Repository.ChangeAsync(c => c.RemoveServer(server.Id));
        await new AtomicJsonStore<ServerCreationPublicationRecord>(Path.Combine(root, "app-settings", "creation-publication", server.Id.ToString("N") + ".json"))
            .SaveAsync(result with { Phase = ServerCreationPublicationPhase.Published });
        var configuration = Path.Combine(server.Directory, "ShooterGame", "Saved", "Config", "WindowsServer", "Game.ini");
        await File.WriteAllTextAsync(configuration, "changed outside creation");
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerCreationPublication.ExecuteAsync(Repository, root, server, null, (_, action, ct) => action(ct)));
        Assert.Empty((await Repository.ReadAsync()).Servers);
        Assert.Equal("changed outside creation", await File.ReadAllTextAsync(configuration));
    }

    [Fact]
    public async Task ManagerSubmissionSharesFileGateAndReusesOneBackgroundJob()
    {
        var (server, staging) = await Prepare();
        await using var jobs = new BackgroundOperations(8, 1);
        using var gate = new SemaphoreSlim(0, 1);
        var checks = 0;
        var routing = new ServerCreationManagerOperations(Repository, root, jobs, gate,
            (_, change, ct) => { checks++; return change(ct); });
        var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "server-creation-publish-start",
            System.Text.Json.JsonSerializer.SerializeToElement(new ServerCreationPublicationRequest(server, null)));
        var first = Assert.IsType<BackgroundOperation>(routing.StartPublication(request));
        var repeated = Assert.IsType<BackgroundOperation>(routing.StartPublication(request));
        Assert.Equal(first.CreatedAt, repeated.CreatedAt);
        Assert.Equal(0, checks); Assert.True(Directory.Exists(staging));
        gate.Release();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!jobs.Find(first.Id)!.FinishedAt.HasValue) await Task.Delay(10, deadline.Token);
        var finished = jobs.Find(first.Id)!;
        Assert.Equal(BackgroundOperationState.Succeeded, finished.State);
        Assert.Equal(1, checks); Assert.Single((await Repository.ReadAsync()).Servers);
        var result = System.Text.Json.JsonSerializer.Deserialize<ServerCreationPublicationRecord>(finished.Result!.Value);
        Assert.Equal(ServerCreationPublicationPhase.Registered, result!.Phase);
        Assert.DoesNotContain("fixture-private-password", System.Text.Json.JsonSerializer.Serialize(finished));
    }

    [Fact]
    public async Task AddingAnotherArkRegistersOnlyTheNewMemberAndPreservesTheFirst()
    {
        var clusterId = Guid.NewGuid(); var (first, _) = await Prepare(clusterId);
        var cluster = new ClusterDefinition(clusterId, "Fixture cluster", ArkEdition.ASE, first.Settings);
        await ServerCreationPublication.ExecuteAsync(Repository, root, first, cluster, (_, action, ct) => action(ct));
        var firstFile = Path.Combine(first.Directory, "ShooterGame", "Saved", "Config", "WindowsServer", "GameUserSettings.ini");
        var before = await File.ReadAllBytesAsync(firstFile);
        var world = Path.Combine(first.Directory, "ShooterGame", "Saved", "SavedArks", "TheIsland.ark");
        Directory.CreateDirectory(Path.GetDirectoryName(world)!); await File.WriteAllTextAsync(world, "existing world state");
        var shared = ManagedClusterStorage.Resolve(await Repository.ReadAsync(), clusterId, root);
        Directory.CreateDirectory(shared.Transfers); await File.WriteAllTextAsync(Path.Combine(shared.Transfers, "player"), "existing transfer state");
        var (second, _) = await Prepare(clusterId, "Second ARK", "Ragnarok");
        await ServerCreationPublication.ExecuteAsync(Repository, root, second, null, (_, action, ct) => action(ct));
        var catalog = await Repository.ReadAsync();
        Assert.Single(catalog.Clusters); Assert.Equal(2, catalog.Servers.Count);
        Assert.All(catalog.Servers, member => Assert.Equal(clusterId, member.ClusterId));
        Assert.Equal(first.Settings.Name, catalog.Servers.Single(s => s.Id == first.Id).Settings.Name);
        Assert.Equal(before, await File.ReadAllBytesAsync(firstFile));
        Assert.Equal("existing world state", await File.ReadAllTextAsync(world));
        Assert.Equal("existing transfer state", await File.ReadAllTextAsync(Path.Combine(shared.Transfers, "player")));
        Assert.NotEqual(first.Settings.GamePort, second.Settings.GamePort);
    }

    [Theory]
    [InlineData("SessionName")]
    [InlineData("MaxPlayers")]
    [InlineData("Port")]
    [InlineData("QueryPort")]
    [InlineData("duplicate")]
    public async Task MismatchedPreparedIdentityDoesNotPublishOrRegister(string key)
    {
        var (server, staging) = await Prepare();
        var path = Path.Combine(staging, "ShooterGame", "Saved", "Config", "WindowsServer", "GameUserSettings.ini");
        var ini = new IniDocument(await File.ReadAllTextAsync(path));
        string changed;
        if (key == "duplicate") changed = ini + "\n[SessionSettings]\nPort=1\n";
        else
        {
            ini.SetValues(key == "MaxPlayers" ? "/Script/Engine.GameSession" : "SessionSettings", key, [key == "SessionName" ? "Another server" : "1"]);
            changed = ini.ToString();
        }
        await File.WriteAllTextAsync(path, changed);
        // Model a prepared package containing inconsistent identity, independently of receipt tampering.
        var receiptPath = Path.Combine(Path.GetDirectoryName(path)!, ".ark-config.json");
        var receipt = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!;
        receipt["UserSettingsSHA256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(changed)));
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerCreationPublication.ExecuteAsync(Repository, root, server, null, (_, action, ct) => action(ct)));
        Assert.True(Directory.Exists(staging)); Assert.False(Directory.Exists(server.Directory));
        Assert.Empty((await Repository.ReadAsync()).Servers);
        Assert.False(Directory.Exists(Path.Combine(root, "app-settings", "creation-publication")));
        Assert.Equal(changed, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-owner")]
    [InlineData("permissions")]
    public async Task PublicationRequiresOriginalPrivateConfigurationOwnership(string problem)
    {
        var (server, staging) = await Prepare();
        var directory = Path.Combine(staging, "ShooterGame", "Saved", "Config", "WindowsServer");
        var receiptPath = Path.Combine(directory, ".ark-config.json");
        if (problem == "missing") File.Delete(receiptPath);
        else if (problem == "wrong-owner")
        {
            var receipt = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!;
            receipt["ServerId"] = Guid.NewGuid().ToString(); await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        }
        else
        {
            var path = Path.Combine(directory, "GameUserSettings.ini");
            if (OperatingSystem.IsWindows())
            {
                var file = new FileInfo(path);
                var access = System.IO.FileSystemAclExtensions.GetAccessControl(file);
                access.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.WorldSid, null), System.Security.AccessControl.FileSystemRights.Read,
                    System.Security.AccessControl.AccessControlType.Allow));
                System.IO.FileSystemAclExtensions.SetAccessControl(file, access);
            }
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        }
        var execute = () => ServerCreationPublication.ExecuteAsync(Repository, root, server, null, (_, action, ct) => action(ct));
        if (problem == "wrong-owner") await Assert.ThrowsAsync<InvalidDataException>(execute);
        else await Assert.ThrowsAnyAsync<IOException>(execute);
        Assert.True(Directory.Exists(staging)); Assert.False(Directory.Exists(server.Directory));
        Assert.Empty((await Repository.ReadAsync()).Servers);
        Assert.False(Directory.Exists(Path.Combine(root, "app-settings", "creation-publication")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
