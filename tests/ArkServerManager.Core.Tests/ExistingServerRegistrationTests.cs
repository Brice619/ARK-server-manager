using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ExistingServerRegistrationTests
{
    [Theory]
    [InlineData(InstallationImportMode.Copy)]
    [InlineData(InstallationImportMode.Move)]
    public async Task ReviewedTransferRegistersDestinationAndPreservesServerFiles(InstallationImportMode mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-transfer-registration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "source"); var management = Path.Combine(root, "managed");
            var platform = OperatingSystem.IsWindows() ? "Windows" : "Linux";
            var binary = Path.Combine(source, "ShooterGame", "Binaries", OperatingSystem.IsWindows() ? "Win64" : "Linux",
                OperatingSystem.IsWindows() ? "ShooterGameServer.exe" : "ShooterGameServer");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!); await File.WriteAllTextAsync(binary, "fixture; never launched");
            var folder = Path.Combine(source, "ShooterGame", "Saved", "Config", platform + "Server"); Directory.CreateDirectory(folder);
            var relativeConfig = Path.GetRelativePath(source, Path.Combine(folder, "GameUserSettings.ini"));
            var configuration = "[SessionSettings]\nSessionName=Existing\n[ServerSettings]\nServerAdminPassword=private-admin\nServerPassword=private-join\nTamingSpeedMultiplier=4\n";
            await File.WriteAllTextAsync(Path.Combine(source, relativeConfig), configuration);
            var relativeWorld = Path.Combine("ShooterGame", "Saved", "SavedArks", "TheIsland.ark");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, relativeWorld))!);
            await File.WriteAllBytesAsync(Path.Combine(source, relativeWorld), [1, 9, 3, 7]);
            var repository = new CatalogRepository(Path.Combine(management, "catalog.json"));
            var reviews = new ExistingServerImportReviewService(repository);
            var review = await reviews.ReadAsync(new(source, ArkEdition.ASE, platform));
            var selection = new ExistingServerRegistrationSelection(review, Guid.NewGuid(), "TheIsland", "Existing", 70, 7777, 27015, 32330, [], true);
            var target = Path.Combine(management, "servers", "Imported");
            var request = new ExistingServerTransferSelection(selection, mode, target, true);
            var checks = 0;
            var registration = new ExistingServerRegistration(repository, reviews, (_, _) => { checks++; return Task.CompletedTask; });
            await Assert.ThrowsAsync<ArgumentException>(() => registration.TransferAndRegisterAsync(request with { TransferConfirmed = false }, management));
            Assert.Equal(0, checks); Assert.False(Directory.Exists(target));
            await Assert.ThrowsAsync<ArgumentException>(() => registration.TransferAndRegisterAsync(request with { Destination = Path.Combine(management, "Dependencies", "Bad") }, management));
            var result = await registration.TransferAndRegisterAsync(request, management);
            Assert.Equal(target, result.Directory); Assert.Equal(mode == InstallationImportMode.Move, result.OriginalRemoved);
            Assert.Null(result.CleanupRequired); Assert.True(checks >= 2);
            var server = Assert.Single((await repository.ReadAsync()).Servers);
            Assert.Equal(selection.ServerId, server.Id); Assert.Equal(target, server.Directory);
            Assert.Equal("4", server.Settings.Values["TamingSpeedMultiplier"]);
            Assert.Equal(configuration, await File.ReadAllTextAsync(Path.Combine(target, relativeConfig)));
            Assert.Equal(new byte[] { 1, 9, 3, 7 }, await File.ReadAllBytesAsync(Path.Combine(target, relativeWorld)));
            Assert.Equal(mode == InstallationImportMode.Copy, Directory.Exists(source));
            Assert.Empty(await new ExistingServerImportJournal(management).ReadAsync());
            if (mode == InstallationImportMode.Copy)
            {
                var another = request with { Registration = selection with { ServerId = Guid.NewGuid(), GamePort = 7787, QueryPort = 27025, RconPort = 32340 } };
                await Assert.ThrowsAsync<IOException>(() => registration.TransferAndRegisterAsync(another, management));
                Assert.Equal(configuration, await File.ReadAllTextAsync(Path.Combine(target, relativeConfig)));
                Assert.Single((await repository.ReadAsync()).Servers);
                var recoveryTarget = Path.Combine(management, "servers", "NeedsRegistration");
                var blocked = another with { Destination = recoveryTarget };
                var lateChecks = 0;
                var lateConflict = new ExistingServerRegistration(repository, reviews, async (_, token) =>
                {
                    if (++lateChecks == 2)
                        await repository.ChangeAsync(current => current.AddServer(new(Guid.NewGuid(), ArkEdition.ASE, "TheIsland",
                            Path.Combine(root, "unrelated-server"), new ServerSettings { Name = "Late port conflict", MaxPlayers = 70,
                                GamePort = 7787, QueryPort = 27025, RconPort = 32340 })), token);
                });
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => lateConflict.TransferAndRegisterAsync(blocked, management));
                Assert.Contains(recoveryTarget, error.Message);
                Assert.Equal(configuration, await File.ReadAllTextAsync(Path.Combine(recoveryTarget, relativeConfig)));
                Assert.Equal(new byte[] { 1, 9, 3, 7 }, await File.ReadAllBytesAsync(Path.Combine(recoveryTarget, relativeWorld)));
                Assert.True(Directory.Exists(source));
                Assert.DoesNotContain((await repository.ReadAsync()).Servers, s => s.Id == blocked.Registration.ServerId);
                var journal = new ExistingServerImportJournal(management);
                var unfinished = Assert.Single(await journal.ReadAsync()); Assert.NotNull(unfinished.Proof);
                var privateRecord = await File.ReadAllTextAsync(Path.Combine(management, "app-settings", "existing-imports", blocked.Registration.ServerId.ToString("N") + ".json"));
                Assert.DoesNotContain("private-admin", privateRecord); Assert.DoesNotContain("private-join", privateRecord);
                var conflict = (await repository.ReadAsync()).Servers.Single(s => s.Settings.Name == "Late port conflict");
                await repository.ChangeAsync(current => current.RemoveServer(conflict.Id));
                var freshRegistration = new ExistingServerRegistration(repository, new ExistingServerImportReviewService(repository), (_, _) => Task.CompletedTask);
                var recovered = await freshRegistration.RecoverPublishedAsync(management, new(blocked.Registration.ServerId, true));
                Assert.Equal(recoveryTarget, recovered.Directory); Assert.False(recovered.OriginalRemoved);
                Assert.Contains((await repository.ReadAsync()).Servers, s => s.Id == blocked.Registration.ServerId && s.Directory == recoveryTarget);
                Assert.Empty(await new ExistingServerImportJournal(management).ReadAsync());
                Assert.Equal(configuration, await File.ReadAllTextAsync(Path.Combine(source, relativeConfig)));
            }
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ConfirmedInPlaceRegistrationPreservesStateAndRefusesStaleRunningOrDuplicateSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-existing-registration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            var platform = OperatingSystem.IsWindows() ? "Windows" : "Linux";
            var binary = Path.Combine(source, "ShooterGame", "Binaries", OperatingSystem.IsWindows() ? "Win64" : "Linux",
                OperatingSystem.IsWindows() ? "ShooterGameServer.exe" : "ShooterGameServer");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!); await File.WriteAllTextAsync(binary, "fixture; never launched");
            var folder = Path.Combine(source, "ShooterGame", "Saved", "Config", platform + "Server"); Directory.CreateDirectory(folder);
            var userPath = Path.Combine(folder, "GameUserSettings.ini");
            var user = "[SessionSettings]\nSessionName=Original\n[ServerSettings]\nServerAdminPassword=private-admin\nServerPassword=private-join\nRCONEnabled=True\nRCONPort=32330\nTamingSpeedMultiplier=8\nActiveMods=123\n";
            await File.WriteAllTextAsync(userPath, user);
            var saved = Path.Combine(source, "ShooterGame", "Saved", "SavedArks"); Directory.CreateDirectory(saved);
            var world = Path.Combine(saved, "TheIsland.ark"); await File.WriteAllTextAsync(world, "preserved world");
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json")); var reviews = new ExistingServerImportReviewService(repository);
            var review = await reviews.ReadAsync(new(source, ArkEdition.ASE, platform));
            var selection = new ExistingServerRegistrationSelection(review, Guid.NewGuid(), "TheIsland", "Original", 70, 7777, 27015, 32330, ["123"], true);
            var stopped = false; var checks = 0;
            var registration = new ExistingServerRegistration(repository, reviews, (_, _) =>
            { checks++; if (!stopped) throw new InvalidOperationException("Fixture running"); return Task.CompletedTask; });
            await Assert.ThrowsAsync<ArgumentException>(() => registration.RegisterInPlaceAsync(selection with { Confirmed = false }));
            Assert.Equal(0, checks);
            await Assert.ThrowsAsync<InvalidOperationException>(() => registration.RegisterInPlaceAsync(selection));
            Assert.Empty((await repository.ReadAsync()).Servers);
            stopped = true; await File.AppendAllTextAsync(userPath, "; changed after review\n");
            await Assert.ThrowsAsync<InvalidOperationException>(() => registration.RegisterInPlaceAsync(selection));
            await File.WriteAllTextAsync(userPath, user);
            var imported = await registration.RegisterInPlaceAsync(selection);
            Assert.Equal(selection.ServerId, imported.Id); Assert.Equal(source, imported.Directory);
            Assert.Equal("8", imported.Settings.Values["TamingSpeedMultiplier"]);
            Assert.DoesNotContain(imported.Settings.Values.Keys, PresetLibrary.IsPasswordSetting);
            Assert.False(imported.Settings.Values.ContainsKey("RCONPort"));
            Assert.Equal(user, await File.ReadAllTextAsync(userPath)); Assert.Equal("preserved world", await File.ReadAllTextAsync(world));
            Assert.False(File.Exists(Path.Combine(folder, "Game.ini")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => registration.RegisterInPlaceAsync(selection));
            Assert.Single((await repository.ReadAsync()).Servers);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
