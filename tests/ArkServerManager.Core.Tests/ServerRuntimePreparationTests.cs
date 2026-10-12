using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class ServerRuntimePreparationTests
{
    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 1)]
    public async Task ReusesReadyLibrariesAndRechecksAfterRepair(bool initiallyReady, bool repairWorks, int expectedRepairs)
    {
        var ready = initiallyReady; var repairs = 0;
        var preparation = new ServerRuntimePreparation(_ => [new("vcruntime140.dll", "fixture", ready, "fixture")],
            (_, _) => { repairs++; ready = repairWorks; return Task.FromResult(repairWorks); });
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASA, "TheIsland_WP", Path.GetTempPath(), new());
        if (!initiallyReady && !repairWorks) await Assert.ThrowsAsync<IOException>(() => preparation.PrepareAsync(server, CancellationToken.None));
        else await preparation.PrepareAsync(server, CancellationToken.None);
        Assert.Equal(expectedRepairs, repairs);
    }
    [Fact]
    public async Task SuccessfulInstallerExitDoesNotOverrideFailedLibraryInspection()
    {
        var preparation = new ServerRuntimePreparation(_ => [new("missing.dll", "fixture", false, "missing")], (_, _) => Task.FromResult(true));
        await Assert.ThrowsAsync<IOException>(() => preparation.PrepareAsync(new(Guid.NewGuid(), ArkEdition.ASA, "TheIsland_WP", Path.GetTempPath(), new()), CancellationToken.None));
    }
}
