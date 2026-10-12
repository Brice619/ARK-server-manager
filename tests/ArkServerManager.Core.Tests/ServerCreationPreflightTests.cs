using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerCreationPreflightTests
{
    [Fact]
    public async Task ChecksPortsWithoutCreatingServerFilesOrRegisteringSetup()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-create-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var id = Guid.NewGuid();
            var request = new ServerCreationCheckRequest(id, ArkEdition.ASE, "TheIsland", "New server", 20, false, null);
            var result = await ServerCreationPreflight.CheckAsync(repository, root, request);
            Assert.Equal(id, result.ServerId);
            new ServerSettings { Name = request.Name, MaxPlayers = request.MaxPlayers, GamePort = result.GamePort,
                QueryPort = result.QueryPort, RconPort = result.RconPort }.Validate();
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Empty((await repository.ReadAsync()).Servers);
            Directory.CreateDirectory(result.Directory);
            await Assert.ThrowsAsync<IOException>(() => ServerCreationPreflight.CheckAsync(repository, root, request));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("../world")]
    [InlineData("TheIsland?listen")]
    [InlineData("TheIsland\n")]
    public async Task InvalidMapCannotReachFilesystemChecks(string map)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-create-invalid-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<ArgumentException>(() => ServerCreationPreflight.CheckAsync(new(Path.Combine(root, "catalog.json")), root,
            new(Guid.NewGuid(), ArkEdition.ASE, map, "Server", 20, false, null)));
        Assert.False(Directory.Exists(root));
    }
}
