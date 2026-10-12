using System.Security.AccessControl;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class InitialServerConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-initial-config-" + Guid.NewGuid().ToString("N"));
    private readonly Guid id = Guid.NewGuid();

    private async Task<string> Ready()
    {
        var staging = Path.Combine(root, "servers", ".ark-create-" + id.ToString("N"));
        Directory.CreateDirectory(staging);
        await new AtomicJsonStore<ServerInstallationRecord>(Path.Combine(root, "app-settings", "creation", id.ToString("N") + ".json"))
            .SaveAsync(new(1, id, ArkEdition.ASE, ServerPlatform.Windows, staging,
                Path.Combine(root, "servers", id.ToString("N")), ServerInstallationPhase.FilesReady));
        return Path.Combine(staging, "ShooterGame", "Saved", "Config", "WindowsServer");
    }

    [Fact]
    public async Task PublishesBothFilesPrivatelyAndRefusesReplacingExistingConfiguration()
    {
        var target = await Ready();
        const string content = "[ServerSettings]\nServerAdminPassword=fixture-secret\n";
        await InitialServerConfiguration.WriteAsync(root, id, content, "[Game]\n", (_, action, ct) => action(ct));
        var path = Path.Combine(target, "GameUserSettings.ini");
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.Equal("[Game]\n", await File.ReadAllTextAsync(Path.Combine(target, "Game.ini")));
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(3, security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Count);
        }
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        var modifiedAt = File.GetLastWriteTimeUtc(path);
        await InitialServerConfiguration.WriteAsync(root, id, content, "[Game]\n", (_, action, ct) => action(ct));
        Assert.Equal(modifiedAt, File.GetLastWriteTimeUtc(path));
        Assert.DoesNotContain("fixture-secret", await File.ReadAllTextAsync(Path.Combine(target, ".ark-config.json")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => InitialServerConfiguration.WriteAsync(root, id, "changed", "changed", (_, action, ct) => action(ct)));
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(target)!, ".ark-config-*"));
    }

    [Fact]
    public async Task DeniedStoppedScopeCreatesNoConfiguration()
    {
        var target = await Ready();
        await Assert.ThrowsAsync<InvalidOperationException>(() => InitialServerConfiguration.WriteAsync(root, id, "settings", "game", (_, _, _) => Task.CompletedTask));
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)!));
    }

    [Fact]
    public async Task RetryRefusesExternallyChangedContentAndKeepsItUntouched()
    {
        var target = await Ready();
        await InitialServerConfiguration.WriteAsync(root, id, "original", "game", (_, action, ct) => action(ct));
        var path = Path.Combine(target, "GameUserSettings.ini");
        await File.WriteAllTextAsync(path, "external change");
        await Assert.ThrowsAsync<InvalidOperationException>(() => InitialServerConfiguration.WriteAsync(root, id, "original", "game", (_, action, ct) => action(ct)));
        Assert.Equal("external change", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task MatchingExistingFilesWithoutOwnershipReceiptAreNotAdopted()
    {
        var target = await Ready(); Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "GameUserSettings.ini"), "same");
        await File.WriteAllTextAsync(Path.Combine(target, "Game.ini"), "game");
        await Assert.ThrowsAsync<InvalidOperationException>(() => InitialServerConfiguration.WriteAsync(root, id, "same", "game", (_, action, ct) => action(ct)));
        Assert.False(File.Exists(Path.Combine(target, ".ark-config.json")));
    }

    [Fact]
    public async Task RetryRefusesWeakenedPasswordFilePermissions()
    {
        var target = await Ready();
        await InitialServerConfiguration.WriteAsync(root, id, "original", "game", (_, action, ct) => action(ct));
        var path = Path.Combine(target, "GameUserSettings.ini");
        if (OperatingSystem.IsWindows())
        {
            var file = new FileInfo(path); var access = file.GetAccessControl(AccessControlSections.Access);
            access.AddAccessRule(new FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            file.SetAccessControl(access);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        await Assert.ThrowsAsync<IOException>(() => InitialServerConfiguration.WriteAsync(root, id, "original", "game", (_, action, ct) => action(ct)));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task BackgroundPreparationRunsOnceAndKeepsContentsOutOfItsResult()
    {
        var target = await Ready();
        await using var jobs = new BackgroundOperations(8, 1);
        using var gate = new SemaphoreSlim(1, 1); var checks = 0;
        var routing = new ServerCreationManagerOperations(new(Path.Combine(root, "catalog.json")), root, jobs, gate,
            (_, action, ct) => { checks++; return action(ct); });
        var selection = new InitialServerConfigurationRequest { ServerId = id,
            UserSettings = "[ServerSettings]\nServerAdminPassword=private-fixture-password\n", GameSettings = "" };
        Assert.DoesNotContain("private-fixture-password", selection.ToString());
        var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "server-creation-configure-start",
            System.Text.Json.JsonSerializer.SerializeToElement(selection));
        var first = Assert.IsType<BackgroundOperation>(routing.StartConfiguration(request));
        Assert.Equal(first.CreatedAt, Assert.IsType<BackgroundOperation>(routing.StartConfiguration(request)).CreatedAt);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!jobs.Find(first.Id)!.FinishedAt.HasValue) await Task.Delay(10, deadline.Token);
        var completed = jobs.Find(first.Id)!;
        Assert.Equal(BackgroundOperationState.Succeeded, completed.State); Assert.Equal(1, checks);
        Assert.Equal(new InitialServerConfigurationResult(id, true),
            System.Text.Json.JsonSerializer.Deserialize<InitialServerConfigurationResult>(completed.Result!.Value));
        Assert.DoesNotContain("private-fixture-password", System.Text.Json.JsonSerializer.Serialize(completed));
        Assert.Contains("private-fixture-password", await File.ReadAllTextAsync(Path.Combine(target, "GameUserSettings.ini")));
    }

    [Theory]
    [InlineData("matching")]
    [InlineData("edition")]
    [InlineData("folder")]
    public async Task GuidedJobOnlyWritesTheMatchingInstalledSetup(string scenario)
    {
        var target = await Ready();
        var final = Path.Combine(root, "servers", id.ToString("N"));
        var server = new ServerDefinition(id, scenario == "edition" ? ArkEdition.ASA : ArkEdition.ASE, "TheIsland",
            scenario == "folder" ? Path.Combine(root, "unrelated") : final,
            new() { Name = "Chosen server", MaxPlayers = 24, GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        await using var jobs = new BackgroundOperations(8, 1);
        using var gate = new SemaphoreSlim(1, 1); var checks = 0;
        var routing = new ServerCreationManagerOperations(new(Path.Combine(root, "catalog.json")), root, jobs, gate,
            (_, action, ct) => { checks++; return action(ct); });
        var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "server-creation-guided-configure-start",
            System.Text.Json.JsonSerializer.SerializeToElement(new GuidedServerConfigurationRequest
                { Server = server, PvE = true, AdministratorPassword = "private-guided-password" }));
        var started = Assert.IsType<BackgroundOperation>(routing.StartGuidedConfiguration(request));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!jobs.Find(started.Id)!.FinishedAt.HasValue) await Task.Delay(10, deadline.Token);
        var completed = jobs.Find(started.Id)!;
        Assert.DoesNotContain("private-guided-password", System.Text.Json.JsonSerializer.Serialize(completed));
        if (scenario == "matching")
        {
            Assert.Equal(BackgroundOperationState.Succeeded, completed.State); Assert.Equal(1, checks);
            var content = new IniDocument(await File.ReadAllTextAsync(Path.Combine(target, "GameUserSettings.ini")));
            Assert.Equal(["Chosen server"], content.GetValues("SessionSettings", "SessionName"));
            Assert.Equal(["24"], content.GetValues("/Script/Engine.GameSession", "MaxPlayers"));
        }
        else
        {
            Assert.Equal(BackgroundOperationState.Failed, completed.State); Assert.Equal(0, checks);
            Assert.False(Directory.Exists(Path.GetDirectoryName(target)!));
        }
        Assert.False(Directory.Exists(final));
        Assert.False(Directory.Exists(Path.Combine(root, "unrelated")));
    }

    [Fact]
    public async Task ConfigurationJobSurvivesDesktopDisconnectOverLocalPipe()
    {
        var target = await Ready();
        await using var jobs = new BackgroundOperations(8, 1);
        using var gate = new SemaphoreSlim(1, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var writes = 0;
        var routing = new ServerCreationManagerOperations(new(Path.Combine(root, "catalog.json")), root, jobs, gate,
            async (_, action, ct) => { writes++; entered.TrySetResult(); await release.Task.WaitAsync(ct); await action(ct); });
        using var hostLifetime = new CancellationTokenSource();
        var host = new LocalManagerServer(root, (message, _) => Task.FromResult<object>(
            message.Operation == "server-creation-configure-start" ? routing.StartConfiguration(message)
                : jobs.Find(System.Text.Json.JsonSerializer.Deserialize<OperationStatusRequest>(message.Payload!.Value)!.Id)!));
        var running = host.RunAsync(hostLifetime.Token);
        try
        {
            var operation = Guid.NewGuid();
            var selection = new InitialServerConfigurationRequest { ServerId = id, UserSettings = "private configuration", GameSettings = "" };
            using var desktopLifetime = new CancellationTokenSource();
            var first = await new LocalManagerClient(root).CallAsync<BackgroundOperation>("server-creation-configure-start",
                selection, TimeSpan.FromSeconds(5), desktopLifetime.Token, operation);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await desktopLifetime.CancelAsync();
            var reopened = new LocalManagerClient(root);
            var repeated = await reopened.CallAsync<BackgroundOperation>("server-creation-configure-start",
                selection, TimeSpan.FromSeconds(5), requestId: operation);
            Assert.Equal(first.CreatedAt, repeated.CreatedAt); Assert.Equal(BackgroundOperationState.Running, repeated.State);
            Assert.False(Directory.Exists(target)); release.TrySetResult();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            BackgroundOperation status;
            do
            {
                status = await reopened.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(operation),
                    TimeSpan.FromSeconds(5), deadline.Token);
                if (!status.FinishedAt.HasValue) await Task.Delay(10, deadline.Token);
            } while (!status.FinishedAt.HasValue);
            Assert.Equal(BackgroundOperationState.Succeeded, status.State); Assert.Equal(1, writes);
            Assert.Equal(new InitialServerConfigurationResult(id, true),
                System.Text.Json.JsonSerializer.Deserialize<InitialServerConfigurationResult>(status.Result!.Value));
            Assert.DoesNotContain("private configuration", System.Text.Json.JsonSerializer.Serialize(status));
            Assert.Equal("private configuration", await File.ReadAllTextAsync(Path.Combine(target, "GameUserSettings.ini")));
        }
        finally { release.TrySetResult(); await hostLifetime.CancelAsync(); await running; }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
