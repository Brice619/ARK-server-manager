using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class CatalogTests
{
    private static ServerSettings Settings(string name, int offset = 0) => new()
    {
        Name = name, GamePort = 7777 + offset, QueryPort = 27015 + offset,
        RconPort = 32330 + offset, Values = new() { ["DifficultyOffset"] = "1" }
    };

    [Fact]
    public void UserOperationsIncludeClusterPeersButNotIndependentServers()
    {
        var catalog = new ServerCatalog();
        var cluster = new ClusterDefinition(Guid.NewGuid(), "Cluster", ArkEdition.ASE, Settings("Cluster"));
        catalog.AddCluster(cluster);
        var first = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", "first", Settings("First"), cluster.Id);
        var second = first with { Id = Guid.NewGuid(), Directory = "second", Settings = Settings("Second", 10) };
        var standalone = first with { Id = Guid.NewGuid(), Directory = "third", Settings = Settings("Third", 20), ClusterId = null };
        catalog.AddServer(first);
        catalog.AddServer(second);
        catalog.AddServer(standalone);
        Assert.Equal(2, catalog.OperationTargets(first.Id).Count);
        Assert.DoesNotContain(standalone.Id, catalog.OperationTargets(first.Id));
        Assert.Single(catalog.OperationTargets(standalone.Id));
    }

    [Fact]
    public void AddingMemberInheritsSettingsWithoutAliasingOrPortCollisions()
    {
        var catalog = new ServerCatalog();
        var cluster = new ClusterDefinition(Guid.NewGuid(), "Cluster", ArkEdition.ASA, Settings("Shared"));
        catalog.AddCluster(cluster);
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASA, "TheIsland_WP", "server", Settings("Original", 10));
        catalog.AddServer(server);
        catalog.AddExistingServerToCluster(server.Id, cluster.Id);
        var actual = Assert.Single(catalog.Servers);
        Assert.Equal("Shared", actual.Settings.Name);
        Assert.Equal(server.Settings.GamePort, actual.Settings.GamePort);
        actual.Settings.Values["DifficultyOffset"] = "99";
        Assert.Equal("1", Assert.Single(catalog.Servers).Settings.Values["DifficultyOffset"]);
        Assert.Throws<InvalidOperationException>(() => catalog.AddExistingServerToCluster(server.Id, cluster.Id));
    }

    [Fact]
    public void RejectsMixedEditionsAndAdjacentGamePortCollisions()
    {
        var catalog = new ServerCatalog();
        var cluster = new ClusterDefinition(Guid.NewGuid(), "Cluster", ArkEdition.ASE, Settings("Shared"));
        catalog.AddCluster(cluster);
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASA, "TheIsland_WP", "server", Settings("Server"));
        catalog.AddServer(server);
        Assert.Throws<ArgumentException>(() => catalog.AddExistingServerToCluster(server.Id, cluster.Id));
        Assert.Throws<ArgumentException>(() => catalog.AddServer(server with { Id = Guid.NewGuid(), Settings = Settings("Other", 1) }));
    }

    [Fact]
    public void RegroupPreservesSettingsModsAndOldCluster()
    {
        var catalog = new ServerCatalog();
        var oldCluster = new ClusterDefinition(Guid.NewGuid(), "Old", ArkEdition.ASE, Settings("Old"));
        var newCluster = oldCluster with { Id = Guid.NewGuid(), Name = "New" };
        catalog.AddCluster(oldCluster); catalog.AddCluster(newCluster);
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", "first", Settings("Personal"), oldCluster.Id)
            { Mods = ["2182894352"] };
        catalog.AddServer(server);
        catalog.SetClusterMembership([server.Id], newCluster.Id);
        var actual = Assert.Single(catalog.Servers);
        Assert.Equal(newCluster.Id, actual.ClusterId);
        Assert.Equal("Personal", actual.Settings.Name);
        Assert.Equal(server.Settings.GamePort, actual.Settings.GamePort);
        Assert.Equal(server.Mods, actual.Mods);
        Assert.Contains(catalog.Clusters, cluster => cluster.Id == oldCluster.Id);
        catalog.SetClusterMembership([server.Id], null);
        Assert.Null(Assert.Single(catalog.Servers).ClusterId);
        Assert.Equal(2, catalog.Clusters.Count);
    }

    [Fact]
    public void RegroupRejectsWholeMixedSelectionWithoutPartialChanges()
    {
        var catalog = new ServerCatalog();
        var cluster = new ClusterDefinition(Guid.NewGuid(), "ASE", ArkEdition.ASE, Settings("ASE"));
        catalog.AddCluster(cluster);
        var ase = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", "ase", Settings("ASE"));
        var asa = ase with { Id = Guid.NewGuid(), Edition = ArkEdition.ASA, Directory = "asa", Settings = Settings("ASA", 10) };
        catalog.AddServer(ase); catalog.AddServer(asa);
        Assert.Throws<ArgumentException>(() => catalog.SetClusterMembership([ase.Id, asa.Id], cluster.Id));
        Assert.All(catalog.Servers, server => Assert.Null(server.ClusterId));
        Assert.Throws<KeyNotFoundException>(() => catalog.SetClusterMembership([ase.Id, Guid.NewGuid()], cluster.Id));
        Assert.All(catalog.Servers, server => Assert.Null(server.ClusterId));
        Assert.Throws<ArgumentException>(() => catalog.SetClusterMembership([ase.Id, ase.Id], cluster.Id));
    }

    [Fact]
    public void EpicClientSelectionRejectsModdedPresetBeforeChangingSettings()
    {
        var catalog = new ServerCatalog();
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", "first", Settings("Original"))
            { AseClients = AseClientAccess.SteamAndEpic };
        catalog.AddServer(server);
        var preset = new ServerPreset("Modded", ArkEdition.ASE, Settings("Replacement") with { MaxPlayers = 20 }, ["2182894352"]);
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyPreset(server.Id, preset));
        Assert.Throws<InvalidOperationException>(() => PresetApplication.Build(server, preset, "[ServerSettings]\n", ""));
        Assert.Equal("Original", Assert.Single(catalog.Servers).Settings.Name);
        Assert.Equal(70, Assert.Single(catalog.Servers).Settings.MaxPlayers);
        Assert.Empty(Assert.Single(catalog.Servers).Mods);
        catalog.UpdateAseClients(server.Id, AseClientAccess.SteamAndEpic, AseClientAccess.Steam);
        catalog.ApplyPreset(server.Id, preset);
        Assert.Equal(20, Assert.Single(catalog.Servers).Settings.MaxPlayers);
        Assert.Throws<InvalidOperationException>(() => catalog.UpdateAseClients(server.Id, AseClientAccess.Steam, AseClientAccess.Epic));
    }

    [Fact]
    public void ClusterDefaultTemplatesDoNotAliasAndCannotCrossEditions()
    {
        var catalog = new ServerCatalog(); var cluster = Guid.NewGuid();
        var defaults = new ServerPreset("Defaults", ArkEdition.ASE, Settings("Cluster"), ["2182894352"])
            { UserSettings = "[ServerSettings]\nServerPVE=True\n", GameSettings = "" };
        catalog.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, Settings("Cluster")) { Defaults = defaults });
        defaults.Mods.Clear(); defaults.Settings.Values["DifficultyOffset"] = "99";
        var snapshot = Assert.Single(catalog.Clusters);
        Assert.Single(snapshot.Defaults!.Mods); Assert.Equal("1", snapshot.Defaults.Settings.Values["DifficultyOffset"]);
        snapshot.Defaults.Mods.Clear();
        Assert.Single(Assert.Single(catalog.Clusters).Defaults!.Mods);
        Assert.Throws<ArgumentException>(() => catalog.SetClusterDefaultPreset(cluster, defaults with { Edition = ArkEdition.ASA }));
    }

    [Fact]
    public void RuntimeCrashAllowsExactlyTwoFailedRestarts()
    {
        var recovery = new CrashRecovery();
        Assert.False(recovery.MayAttemptStartup);
        recovery.RuntimeCrashed();
        Assert.True(recovery.MayAttemptStartup);
        recovery.StartupFailed();
        Assert.True(recovery.MayAttemptStartup);
        recovery.StartupFailed();
        Assert.False(recovery.MayAttemptStartup);
        Assert.Throws<InvalidOperationException>(recovery.StartupFailed);
        recovery.StartupSucceeded();
        Assert.False(recovery.Recovering);
    }

    [Fact]
    public void PresetReplacesSettingsAndModsWithoutMutatingSource()
    {
        var catalog = new ServerCatalog();
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", "server", Settings("Old")) { Mods = ["old"] };
        catalog.AddServer(server);
        var preset = new ServerPreset("New", ArkEdition.ASE, Settings("New"), ["123", "456"]);
        catalog.ApplyPreset(server.Id, preset);
        preset.Mods.Clear();
        Assert.Equal(new[] { "123", "456" }, Assert.Single(catalog.Servers).Mods);
        Assert.Equal("New", Assert.Single(catalog.Servers).Settings.Name);
    }
}
