using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class PresetApplicationQueueTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-preset-queue-" + Guid.NewGuid().ToString("N"));

    private async Task<(CatalogRepository Repository, ReviewedPresetApplication Application, string Config, string QueueFile)> FixtureAsync()
    {
        var directory = Path.Combine(root, "server");
        var binary = Path.Combine(directory, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!); await File.WriteAllTextAsync(binary, "fixture");
        var config = Path.Combine(directory, "ShooterGame", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "GameUserSettings.ini"),
            "[ServerSettings]\nServerAdminPassword=private-admin\nServerPassword=private-join\nRCONEnabled=True\nRCONPort=32330\nActiveMods=123,456\n");
        var world = Path.Combine(directory, "ShooterGame", "Saved", "SavedArks");
        Directory.CreateDirectory(world); await File.WriteAllTextAsync(Path.Combine(world, "TheIsland.ark"), "world-state");
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", directory,
            new() { Name = "Current", MaxPlayers = 20, GamePort = 7777, QueryPort = 27015, RconPort = 32330 }) { Mods = ["123", "456"] };
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        await repository.ChangeAsync(c => c.AddServer(server));
        var library = new PresetLibrary(Path.Combine(root, "presets.json"));
        var preset = await library.SaveAsync(new("Rates", ArkEdition.ASE, server.Settings with { MaxPlayers = 40 }, ["123"])
        {
            UserSettings = "[ServerSettings]\nTamingSpeedMultiplier=5\n",
            GameSettings = "[/Script/ShooterGame.ShooterGameMode]\nRepeated=one\nRepeated=two\n"
        });
        var application = await new PresetApplicationReviewService(repository, library).PrepareAsync(new(server.Id, preset.Id));
        return (repository, application, config, Path.Combine(root, "pending.json"));
    }

    [Fact]
    public async Task QueuePersistsWithoutChangingRunningConfigurationAndAppliesSelectedWorldOnly()
    {
        var f = await FixtureAsync();
        var original = await File.ReadAllTextAsync(Path.Combine(f.Config, "GameUserSettings.ini"));
        var stopped = false; var cleanup = new List<string>();
        PresetApplicationQueue NewQueue() => new(f.Repository, f.QueueFile, (server, ct) =>
        {
            Assert.Equal(f.Application.Server.Id, server.Id);
            if (!stopped) throw new InvalidOperationException("still running");
            return Task.CompletedTask;
        }, (server, removed, ct) => { cleanup.AddRange(removed); return Task.CompletedTask; });
        await NewQueue().EnqueueAsync(f.Application);
        await NewQueue().EnqueueAsync(f.Application); // Same reviewed request is idempotent.
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(f.Config, "GameUserSettings.ini")));
        Assert.False(File.Exists(Path.Combine(f.Config, "Game.ini")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => NewQueue().ApplyBeforeStartAsync(f.Application.Server.Id));
        Assert.False((await NewQueue().ReadAsync(f.Application.Server.Id))!.Started);
        stopped = true;
        await NewQueue().ApplyBeforeStartAsync(f.Application.Server.Id);
        Assert.Null(await NewQueue().ReadAsync(f.Application.Server.Id));
        var server = (await f.Repository.ReadAsync()).Servers.Single();
        Assert.Equal(40, server.Settings.MaxPlayers); Assert.Equal(["123"], server.Mods);
        Assert.Equal(["456"], cleanup);
        var user = new IniDocument(await File.ReadAllTextAsync(Path.Combine(f.Config, "GameUserSettings.ini")));
        Assert.Equal(["private-admin"], user.GetValues("ServerSettings", "ServerAdminPassword"));
        Assert.Equal(["private-join"], user.GetValues("ServerSettings", "ServerPassword"));
        Assert.Equal(["123"], user.GetValues("ServerSettings", "ActiveMods"));
        var game = new IniDocument(await File.ReadAllTextAsync(Path.Combine(f.Config, "Game.ini")));
        Assert.Equal(["one", "two"], game.GetValues("/Script/ShooterGame.ShooterGameMode", "Repeated"));
        Assert.Equal("world-state", await File.ReadAllTextAsync(Path.Combine(server.Directory, "ShooterGame", "Saved", "SavedArks", "TheIsland.ark")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialConfigurationWriteSurvivesNewQueueInstanceAndRefusesUnrelatedEdits(bool externalEdit)
    {
        var f = await FixtureAsync(); var calls = 0;
        var first = new PresetApplicationQueue(f.Repository, f.QueueFile, (server, ct) =>
        {
            if (++calls == 3) throw new IOException("simulated failure before user configuration publication");
            return Task.CompletedTask;
        }, (server, removed, ct) => Task.CompletedTask);
        await first.EnqueueAsync(f.Application);
        await Assert.ThrowsAsync<IOException>(() => first.ApplyBeforeStartAsync(f.Application.Server.Id));
        Assert.Equal(f.Application.Plan.GameSettings, await File.ReadAllTextAsync(Path.Combine(f.Config, "Game.ini")));
        Assert.True((await first.ReadAsync(f.Application.Server.Id))!.Started);
        Assert.Equal(20, (await f.Repository.ReadAsync()).Servers.Single().Settings.MaxPlayers);
        if (externalEdit) await File.AppendAllTextAsync(Path.Combine(f.Config, "GameUserSettings.ini"), "; external edit\n");
        var beforeRetry = await File.ReadAllTextAsync(Path.Combine(f.Config, "GameUserSettings.ini"));
        var retry = new PresetApplicationQueue(f.Repository, f.QueueFile, (server, ct) => Task.CompletedTask,
            (server, removed, ct) => Task.CompletedTask);
        if (externalEdit)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => retry.ApplyBeforeStartAsync(f.Application.Server.Id));
            Assert.Equal(beforeRetry, await File.ReadAllTextAsync(Path.Combine(f.Config, "GameUserSettings.ini")));
            Assert.NotNull(await retry.ReadAsync(f.Application.Server.Id));
        }
        else
        {
            await retry.ApplyBeforeStartAsync(f.Application.Server.Id);
            Assert.Equal(f.Application.Plan.UserSettings, await File.ReadAllTextAsync(Path.Combine(f.Config, "GameUserSettings.ini")));
            Assert.Null(await retry.ReadAsync(f.Application.Server.Id));
        }
    }

    [Fact]
    public async Task CleanupFailureRetainsOriginalRemovedModMembershipUntilRetrySucceeds()
    {
        var f = await FixtureAsync(); var fail = true; var removedAttempts = new List<string>();
        PresetApplicationQueue NewQueue() => new(f.Repository, f.QueueFile, (server, ct) => Task.CompletedTask,
            (server, removed, ct) =>
            {
                Assert.Equal(["123", "456"], server.Mods);
                removedAttempts.AddRange(removed);
                if (fail) throw new IOException("locked removed mod file");
                return Task.CompletedTask;
            });
        await NewQueue().EnqueueAsync(f.Application);
        await Assert.ThrowsAsync<IOException>(() => NewQueue().ApplyBeforeStartAsync(f.Application.Server.Id));
        Assert.Equal(["123"], (await f.Repository.ReadAsync()).Servers.Single().Mods);
        Assert.NotNull(await NewQueue().ReadAsync(f.Application.Server.Id));
        fail = false;
        await NewQueue().ApplyBeforeStartAsync(f.Application.Server.Id);
        Assert.Equal(["456", "456"], removedAttempts);
        Assert.Null(await NewQueue().ReadAsync(f.Application.Server.Id));
    }

    [Fact]
    public async Task ChangedSourceIsRefusedBeforeCreatingTheOtherConfigurationFile()
    {
        var f = await FixtureAsync();
        var queue = new PresetApplicationQueue(f.Repository, f.QueueFile, (server, ct) => Task.CompletedTask,
            (server, removed, ct) => throw new InvalidOperationException("cleanup must not run"));
        await queue.EnqueueAsync(f.Application);
        var userPath = Path.Combine(f.Config, "GameUserSettings.ini");
        await File.AppendAllTextAsync(userPath, "; externally edited after saving\n");
        var changed = await File.ReadAllTextAsync(userPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ApplyBeforeStartAsync(f.Application.Server.Id));
        Assert.Equal(changed, await File.ReadAllTextAsync(userPath));
        Assert.False(File.Exists(Path.Combine(f.Config, "Game.ini")));
        Assert.False((await queue.ReadAsync(f.Application.Server.Id))!.Started);
        Assert.Equal(20, (await f.Repository.ReadAsync()).Servers.Single().Settings.MaxPlayers);
    }

    [Fact]
    public async Task UnstartedCancellationRequiresReviewedIdentityAndPreservesExternalEdits()
    {
        var f = await FixtureAsync();
        var queue = new PresetApplicationQueue(f.Repository, f.QueueFile,
            (server, ct) => throw new InvalidOperationException("No stopped check should run during cancellation."),
            (server, removed, ct) => throw new InvalidOperationException("No cleanup should run during cancellation."));
        await queue.EnqueueAsync(f.Application);
        var status = await queue.ReadStatusAsync(f.Application.Server.Id);
        Assert.False(status.Pending!.Started);
        Assert.DoesNotContain("private-admin", System.Text.Json.JsonSerializer.Serialize(status));
        Assert.DoesNotContain("private-join", System.Text.Json.JsonSerializer.Serialize(status));
        await File.AppendAllTextAsync(Path.Combine(f.Config, "GameUserSettings.ini"), "; external edit\n");
        var changed = await File.ReadAllTextAsync(Path.Combine(f.Config, "GameUserSettings.ini"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DiscardUnstartedAsync(new(f.Application.Server.Id, new string('0', 64), true)));
        Assert.NotNull((await queue.ReadStatusAsync(f.Application.Server.Id)).Pending);
        await Assert.ThrowsAsync<ArgumentException>(() => queue.DiscardUnstartedAsync(new(f.Application.Server.Id, f.Application.Review.ReviewToken, false)));
        await queue.DiscardUnstartedAsync(new(f.Application.Server.Id, f.Application.Review.ReviewToken, true));
        Assert.Null((await queue.ReadStatusAsync(f.Application.Server.Id)).Pending);
        Assert.Equal(changed, await File.ReadAllTextAsync(Path.Combine(f.Config, "GameUserSettings.ini")));
        Assert.False(File.Exists(Path.Combine(f.Config, "Game.ini")));
        Assert.Equal(20, (await f.Repository.ReadAsync()).Servers.Single().Settings.MaxPlayers);
    }

    [Fact]
    public async Task PartiallyAppliedPresetCannotLoseItsRecoveryRecordThroughCancellation()
    {
        var f = await FixtureAsync(); var calls = 0;
        var queue = new PresetApplicationQueue(f.Repository, f.QueueFile, (server, ct) =>
        {
            if (++calls == 3) throw new IOException("simulated interruption");
            return Task.CompletedTask;
        }, (server, removed, ct) => Task.CompletedTask);
        await queue.EnqueueAsync(f.Application);
        await Assert.ThrowsAsync<IOException>(() => queue.ApplyBeforeStartAsync(f.Application.Server.Id));
        Assert.True((await queue.ReadStatusAsync(f.Application.Server.Id)).Pending!.Started);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DiscardUnstartedAsync(new(f.Application.Server.Id, f.Application.Review.ReviewToken, true)));
        Assert.NotNull(await queue.ReadAsync(f.Application.Server.Id));
        Assert.Equal(f.Application.Plan.GameSettings, await File.ReadAllTextAsync(Path.Combine(f.Config, "Game.ini")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
