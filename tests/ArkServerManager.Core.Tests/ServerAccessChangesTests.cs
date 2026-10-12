using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Core.Tests;

public sealed class ServerAccessChangesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-access-changes-" + Guid.NewGuid().ToString("N"));
    private async Task<(CatalogRepository Repository, Guid Id, string Config)> FixtureAsync()
    {
        var binary = Path.Combine(root, "server", "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!); await File.WriteAllTextAsync(binary, "fixture");
        var config = Path.Combine(root, "server", "ShooterGame", "Saved", "Config", "WindowsServer", "GameUserSettings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        await File.WriteAllTextAsync(config, "[ServerSettings]\nServerAdminPassword=old-admin\nServerPassword=old-join\nRCONEnabled=True\nRCONPort=32330\nKeepMe=original\n");
        var id = Guid.NewGuid(); var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        await repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", Path.Combine(root, "server"),
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 })));
        return (repository, id, config);
    }

    [Fact]
    public async Task QueuedPatchKeepsCurrentAdminAndAppliesAlongsideOtherSettingsWithoutExposingPasswords()
    {
        var f = await FixtureAsync(); var stopped = false;
        var changes = new ServerAccessChanges(f.Repository, Path.Combine(root, "pending.json"), (_, _) =>
            stopped ? Task.CompletedTask : throw new InvalidOperationException("still running"));
        var review = await changes.ReadAsync(f.Id);
        Assert.True(review.AdministratorConfigured); Assert.True(review.JoinPasswordConfigured);
        Assert.DoesNotContain("old-admin", JsonSerializer.Serialize(review)); Assert.DoesNotContain("old-join", JsonSerializer.Serialize(review));
        var edit = new ServerAccessChange(review, null, "new-join");
        await changes.QueueAsync(edit);
        Assert.DoesNotContain("new-join", edit.ToString());
        Assert.Contains("ServerPassword=old-join", await File.ReadAllTextAsync(f.Config));
        await Assert.ThrowsAsync<InvalidOperationException>(() => changes.ApplyBeforeStartAsync(f.Id));
        await File.AppendAllTextAsync(f.Config, "; unrelated edit\nTamingSpeedMultiplier=7\n");
        var server = (await f.Repository.ReadAsync()).Servers.Single();
        await f.Repository.ChangeAsync(c => c.UpdateSettings(f.Id, server.Settings with { RconPort = 34000 }));
        stopped = true; await changes.ApplyBeforeStartAsync(f.Id);
        var text = await File.ReadAllTextAsync(f.Config);
        Assert.Contains("ServerAdminPassword=old-admin", text); Assert.Contains("ServerPassword=new-join", text);
        Assert.Contains("RCONPort=34000", text); Assert.Contains("KeepMe=original", text); Assert.Contains("; unrelated edit", text);
        Assert.Null((await changes.ReadAsync(f.Id)).PendingId);
    }

    [Fact]
    public async Task ExternalTargetedPasswordEditIsPreservedAndUnstartedQueueCanBeCanceled()
    {
        var f = await FixtureAsync(); var changes = new ServerAccessChanges(f.Repository, Path.Combine(root, "pending.json"), (_, _) => Task.CompletedTask);
        await changes.QueueAsync(new(await changes.ReadAsync(f.Id), "new-admin", null));
        await File.WriteAllTextAsync(f.Config, (await File.ReadAllTextAsync(f.Config)).Replace("old-admin", "external-admin"));
        var before = await File.ReadAllBytesAsync(f.Config);
        await Assert.ThrowsAsync<InvalidOperationException>(() => changes.ApplyBeforeStartAsync(f.Id));
        var review = await changes.ReadAsync(f.Id); Assert.NotNull(review.PendingId); Assert.False(review.Applying);
        await Assert.ThrowsAsync<ArgumentException>(() => changes.DiscardAsync(f.Id, review.PendingId!.Value, false));
        await changes.DiscardAsync(f.Id, review.PendingId!.Value, true);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Config)); Assert.Null((await changes.ReadAsync(f.Id)).PendingId);
    }

    [Fact]
    public async Task InterruptedWriteKeepsRecoveryRecordAndRetriesWithoutDiscardingUnrelatedText()
    {
        var f = await FixtureAsync(); var calls = 0; var fail = true;
        ServerAccessChanges Changes() => new(f.Repository, Path.Combine(root, "pending.json"), (_, _) =>
        {
            if (fail && ++calls == 2) throw new IOException("interrupted write");
            return Task.CompletedTask;
        });
        await Changes().QueueAsync(new(await Changes().ReadAsync(f.Id), "new-admin", ""));
        await Assert.ThrowsAsync<IOException>(() => Changes().ApplyBeforeStartAsync(f.Id));
        var pending = await Changes().ReadAsync(f.Id); Assert.True(pending.Applying);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Changes().DiscardAsync(f.Id, pending.PendingId!.Value, true));
        await File.AppendAllTextAsync(f.Config, "; keep after interrupted attempt\n");
        fail = false; await Changes().ApplyBeforeStartAsync(f.Id);
        var ini = new IniDocument(await File.ReadAllTextAsync(f.Config));
        Assert.Equal(["new-admin"], ini.GetValues("ServerSettings", "ServerAdminPassword"));
        Assert.Equal([""], ini.GetValues("ServerSettings", "ServerPassword"));
        Assert.Contains("; keep after interrupted attempt", ini.ToString()); Assert.Null((await Changes().ReadAsync(f.Id)).PendingId);
    }

    [Fact]
    public async Task StaleReviewCannotQueueChanges()
    {
        var f = await FixtureAsync(); var changes = new ServerAccessChanges(f.Repository, Path.Combine(root, "pending.json"), (_, _) => Task.CompletedTask);
        var review = await changes.ReadAsync(f.Id);
        await File.AppendAllTextAsync(f.Config, "; edited after review\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => changes.QueueAsync(new(review, "new-admin", null)));
        Assert.Null((await changes.ReadAsync(f.Id)).PendingId);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
