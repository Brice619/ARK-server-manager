using System.Text;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class PendingConfigurationEditsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task FreshQueueAppliesReviewedTextPreservesEncodingAndRetainsExternalConflicts(int interruption)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-pending-configuration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "server"),
                new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
            await repository.ChangeAsync(c => c.AddServer(server));
            var binary = Path.Combine(server.Directory, "ShooterGame", "Binaries", OperatingSystem.IsWindows() ? "Win64" : "Linux",
                OperatingSystem.IsWindows() ? "ShooterGameServer.exe" : "ShooterGameServer");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!); await File.WriteAllTextAsync(binary, "never launched");
            var config = Path.Combine(server.Directory, "ShooterGame", "Saved", "Config", OperatingSystem.IsWindows() ? "WindowsServer" : "LinuxServer");
            Directory.CreateDirectory(config); var user = Path.Combine(config, "GameUserSettings.ini");
            const string original = "; keep comment\r\n[ServerSettings]\r\nServerAdminPassword=private-fixture\r\nTamingSpeedMultiplier=1\r\n";
            var desired = original.Replace("TamingSpeedMultiplier=1", "TamingSpeedMultiplier=4");
            await File.WriteAllTextAsync(user, original, Encoding.Unicode);
            var source = await ServerConfigurationReader.ReadAsync(repository, new(server.Id, "GameUserSettings.ini"));
            var queueFile = Path.Combine(root, "pending.json"); var checks = 0;
            Task Stopped(ServerDefinition selected, CancellationToken _) { Assert.Equal(server.Id, selected.Id); checks++; return Task.CompletedTask; }
            var queue = new PendingConfigurationEdits(repository, queueFile, Stopped);
            var operation = Guid.NewGuid(); var edit = new ServerConfigurationEdit(server.Id, "GameUserSettings.ini", source.SourceSHA256!, desired);
            await queue.EnqueueAsync(operation, edit); await queue.EnqueueAsync(operation, edit);
            Assert.Equal(original, await File.ReadAllTextAsync(user)); Assert.Equal(0, checks);
            Assert.DoesNotContain("private-fixture", System.Text.Json.JsonSerializer.Serialize(await queue.ReadAsync(server.Id)));
            if (interruption is 1 or 3)
            {
                await File.WriteAllTextAsync(user, original.Replace("=1", "=7"), Encoding.Unicode);
                var store = new AtomicJsonStore<PendingConfigurationDocument>(queueFile, true);
                var saved = (await store.LoadAsync())!;
                await store.SaveAsync(saved with { Changes = saved.Changes.Select(p => p with { Started = true }).ToList() });
            }
            if (interruption == 2)
            {
                var store = new AtomicJsonStore<PendingConfigurationDocument>(queueFile, true);
                var saved = (await store.LoadAsync())!;
                await store.SaveAsync(saved with { Changes = saved.Changes.Select(p => p with { Started = true }).ToList() });
                await File.WriteAllTextAsync(user, desired, Encoding.Unicode);
                await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DiscardAsync(new(server.Id, operation, true)));
            }
            if (interruption >= 4)
            {
                var store = new AtomicJsonStore<PendingConfigurationDocument>(queueFile, true);
                var saved = (await store.LoadAsync())!;
                await store.SaveAsync(saved with { Changes = saved.Changes.Select(p => p with { Started = true, SourceMissing = interruption == 6 }).ToList() });
                File.Delete(user);
                if (interruption == 6) await File.WriteAllTextAsync(user, desired, new UTF8Encoding(false));
            }
            var fresh = new PendingConfigurationEdits(new CatalogRepository(Path.Combine(root, "catalog.json")), queueFile, Stopped);
            var beforeReview = File.Exists(user) ? await File.ReadAllTextAsync(user) : null;
            var review = await fresh.ReviewAsync(new(server.Id, operation));
            Assert.Equal(desired, review.Text); Assert.Equal(interruption != 0, review.Started);
            Assert.DoesNotContain("private-fixture", review.ToString()); Assert.Equal(beforeReview, File.Exists(user) ? await File.ReadAllTextAsync(user) : null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fresh.ReviewAsync(new(Guid.NewGuid(), operation)));
            if (interruption is 1 or 3)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => fresh.ApplyBeforeStartAsync(server.Id));
                Assert.Single(await fresh.ReadAsync(server.Id)); Assert.Contains("=7", await File.ReadAllTextAsync(user));
                var recovery = await fresh.ReviewResolutionAsync(new(server.Id, operation));
                Assert.Contains("=7", recovery.CurrentText); Assert.Equal(desired, recovery.SavedText);
                Assert.DoesNotContain("private-fixture", recovery.ToString());
                var changedAgain = original.Replace("=1", "=8");
                await File.WriteAllTextAsync(user, changedAgain, Encoding.Unicode);
                var choice = interruption == 1 ? ConfigurationResolutionChoice.UseSavedText : ConfigurationResolutionChoice.KeepCurrentFile;
                await Assert.ThrowsAsync<InvalidOperationException>(() => fresh.ResolveAsync(Guid.NewGuid(), new(server.Id, operation, recovery.Token, choice, true)));
                Assert.Single(await fresh.ReadAsync(server.Id));
                var gamePath = Path.Combine(config, "Game.ini");
                const string otherFile = "[Other]\nOption=original\n";
                await File.WriteAllTextAsync(gamePath, otherFile);
                var game = await ServerConfigurationReader.ReadAsync(repository, new(server.Id, "Game.ini"));
                await fresh.EnqueueAsync(Guid.NewGuid(), new(server.Id, "Game.ini", game.SourceSHA256!, "[Other]\nOption=pending\n"));
                recovery = await fresh.ReviewResolutionAsync(new(server.Id, operation));
                var file = await fresh.ResolveAsync(Guid.NewGuid(), new(server.Id, operation, recovery.Token, choice, true));
                if (choice == ConfigurationResolutionChoice.UseSavedText) await fresh.ApplyBeforeStartAsync(server.Id, default, file);
                Assert.Equal(choice == ConfigurationResolutionChoice.UseSavedText ? desired : changedAgain, await File.ReadAllTextAsync(user));
                Assert.Equal(otherFile, await File.ReadAllTextAsync(gamePath));
                Assert.Equal("Game.ini", Assert.Single(await fresh.ReadAsync(server.Id)).FileName);
            }
            else if (interruption >= 4)
            {
                if (interruption == 6)
                {
                    await fresh.ApplyBeforeStartAsync(server.Id);
                    Assert.Empty(await fresh.ReadAsync(server.Id)); Assert.Equal(desired, await File.ReadAllTextAsync(user));
                }
                else
                {
                    var recovery = await fresh.ReviewResolutionAsync(new(server.Id, operation));
                    Assert.True(recovery.CurrentMissing); Assert.Null(recovery.CurrentSourceSHA256); Assert.Empty(recovery.CurrentText);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => fresh.ResolveAsync(Guid.NewGuid(), new(server.Id, operation, recovery.Token, ConfigurationResolutionChoice.KeepCurrentFile, true)));
                    var restoredId = Guid.NewGuid();
                    await fresh.ResolveAsync(restoredId, new(server.Id, operation, recovery.Token, ConfigurationResolutionChoice.UseSavedText, true));
                    if (interruption == 5)
                    {
                        const string appeared = "[External]\nOption=keep\n";
                        await File.WriteAllTextAsync(user, appeared);
                        await Assert.ThrowsAsync<InvalidOperationException>(() => fresh.ApplyBeforeStartAsync(server.Id));
                        Assert.Equal(appeared, await File.ReadAllTextAsync(user));
                        Assert.Equal(restoredId, Assert.Single(await fresh.ReadAsync(server.Id)).Id);
                    }
                    else
                    {
                        await fresh.ApplyBeforeStartAsync(server.Id); Assert.Empty(await fresh.ReadAsync(server.Id));
                        Assert.Equal(desired, await File.ReadAllTextAsync(user));
                        if (OperatingSystem.IsWindows()) Assert.True(new FileInfo(user).GetAccessControl().AreAccessRulesProtected);
                    }
                }
            }
            else
            {
                await fresh.ApplyBeforeStartAsync(server.Id); await fresh.ApplyBeforeStartAsync(server.Id);
                Assert.Empty(await fresh.ReadAsync(server.Id)); Assert.Equal(desired, await File.ReadAllTextAsync(user));
                Assert.True((await File.ReadAllBytesAsync(user)).Take(2).SequenceEqual(Encoding.Unicode.GetPreamble()));
                if (interruption == 0)
                {
                    var loaded = await ServerConfigurationReader.ReadAsync(repository, new(server.Id, "GameUserSettings.ini"));
                    var canceledId = Guid.NewGuid();
                    await fresh.EnqueueAsync(canceledId, new(server.Id, "GameUserSettings.ini", loaded.SourceSHA256!, desired.Replace("=4", "=5")));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => fresh.DiscardAsync(new(server.Id, Guid.NewGuid(), true)));
                    await fresh.DiscardAsync(new(server.Id, canceledId, true));
                    Assert.Empty(await fresh.ReadAsync(server.Id)); Assert.Equal(desired, await File.ReadAllTextAsync(user));
                }
            }
            Assert.True(checks > 0);
        }
        finally { Directory.Delete(root, true); }
    }
}
