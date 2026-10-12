using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class DeletedServerRecordCleanupTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task OnlyOwnedRecordsWithRemovedNetworkingAreDeleted(bool removed, bool setupFinished)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-deleted-records-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var server = Guid.NewGuid(); var operation = Guid.NewGuid(); var original = Path.Combine(root, "Servers", "deleted");
            var deletion = new ServerDeletionRecord(1, operation, server, "Deleted", original, Path.Combine(root, "Servers", ".ark-delete-" + operation.ToString("N")),
                Guid.NewGuid(), [], ServerDeletionPhase.FilesRemoved);
            var repairPath = Path.Combine(root, "app-settings", "repair-" + server.ToString("N") + ".json");
            var firewallPath = Path.Combine(root, "app-settings", "firewall-" + server.ToString("N") + ".json");
            await new AtomicJsonStore<ServerRepairRecord>(repairPath).SaveAsync(new(1, Guid.NewGuid(), server, original, false, ServerRepairPhase.Validating));
            await new AtomicJsonStore<FirewallSetupRecord>(firewallPath).SaveAsync(new(1, server, original, "owned", true, removed));
            var creationPath = Path.Combine(root, "app-settings", "creation", server.ToString("N") + ".json");
            var publicationPath = Path.Combine(root, "app-settings", "creation-publication", server.ToString("N") + ".json");
            await new AtomicJsonStore<ServerInstallationRecord>(creationPath).SaveAsync(new(1, server, ArkEdition.ASE, ServerPlatform.Windows,
                Path.Combine(root, "Servers", "staging"), original, ServerInstallationPhase.FilesReady));
            await new AtomicJsonStore<ServerCreationPublicationRecord>(publicationPath).SaveAsync(new(1, server, new('a', 64), new('b', 64), new('c', 64),
                setupFinished ? ServerCreationPublicationPhase.Registered : ServerCreationPublicationPhase.Published));
            var sibling = Path.Combine(root, "app-settings", "repair-" + Guid.NewGuid().ToString("N") + ".json"); await File.WriteAllTextAsync(sibling, "keep");
            var shared = Path.Combine(root, "Clusters", "shared"); Directory.CreateDirectory(shared); await File.WriteAllTextAsync(Path.Combine(shared, "player"), "keep");
            var catalog = new CatalogDocument(1, [], []);
            if (!removed || !setupFinished)
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => DeletedServerRecordCleanup.RemoveAsync(catalog, deletion, root));
                Assert.True(File.Exists(repairPath)); Assert.True(File.Exists(firewallPath));
                Assert.True(File.Exists(creationPath)); Assert.True(File.Exists(publicationPath));
            }
            else
            {
                await DeletedServerRecordCleanup.RemoveAsync(catalog, deletion, root);
                await DeletedServerRecordCleanup.RemoveAsync(catalog, deletion, root);
                Assert.False(File.Exists(repairPath)); Assert.False(File.Exists(firewallPath));
                Assert.False(File.Exists(creationPath)); Assert.False(File.Exists(publicationPath)); Assert.False(File.Exists(creationPath + ".lock"));
            }
            Assert.Equal("keep", await File.ReadAllTextAsync(sibling)); Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(shared, "player")));
        }
        finally { Directory.Delete(root, true); }
    }
}
