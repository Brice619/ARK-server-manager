using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerStartupPreferencesTests
{
    [Fact]
    public void ClusterChoiceIncludesNewMembersAndDeletedOrRegroupedStandaloneIsInert()
    {
        var cluster = Guid.NewGuid(); var first = Guid.NewGuid(); var added = Guid.NewGuid(); var moved = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        var settings = new ServerSettings { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 };
        var document = new CatalogDocument(1, [new(cluster, "Cluster", ArkEdition.ASA, settings)],
            [new(first, ArkEdition.ASA, "TheIsland_WP", "first", settings, cluster), new(added, ArkEdition.ASA, "ScorchedEarth_WP", "added", settings, cluster),
             new(moved, ArkEdition.ASA, "TheIsland_WP", "moved", settings, Guid.NewGuid()), new(unrelated, ArkEdition.ASA, "TheIsland_WP", "other", settings)]);
        var selection = new ServerStartupPreferences(1, [moved, Guid.NewGuid()], [cluster, Guid.NewGuid()]);
        Assert.Equal(new[] { first, added }, selection.Resolve(document));
        Assert.Empty(ServerStartupPreferences.Empty.Resolve(document));
    }

    [Fact]
    public async Task ConfirmedSelectionPersistsWithoutStartingOrChangingCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-startup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var id = Guid.NewGuid(); var settings = new ServerSettings { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 };
            await repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", Path.Combine(root, "server"), settings)));
            var before = await File.ReadAllBytesAsync(Path.Combine(root, "catalog.json"));
            var store = new ServerStartupPreferenceStore(root, repository);
            Assert.Empty((await store.ReadAsync()).Servers);
            var preference = new ServerStartupPreferences(1, [id], []);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new(preference, false)));
            Assert.False(File.Exists(Path.Combine(root, "app-settings", "server-startup.json")));
            await store.SaveAsync(new(preference, true));
            var recovered = await new ServerStartupPreferenceStore(root, repository).ReadAsync();
            Assert.Equal(id, Assert.Single(recovered.Servers));
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(root, "catalog.json")));
            Assert.False(Directory.Exists(Path.Combine(root, "server")));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new(new(1, [Guid.NewGuid()], []), true)));
            Assert.Equal(id, Assert.Single((await store.ReadAsync()).Servers));
            var deletion = new ServerDeletionRecord(1, Guid.NewGuid(), id, "Fixture", Path.Combine(root, "server"),
                Path.Combine(root, "staged"), null, [], ServerDeletionPhase.FilesRemoved);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveDeletedAsync(deletion));
            Assert.Equal(id, Assert.Single((await store.ReadAsync()).Servers));
            await repository.ChangeAsync(c => c.RemoveServer(id));
            await store.RemoveDeletedAsync(deletion);
            await store.RemoveDeletedAsync(deletion);
            Assert.Empty((await new ServerStartupPreferenceStore(root, repository).ReadAsync()).Servers);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
