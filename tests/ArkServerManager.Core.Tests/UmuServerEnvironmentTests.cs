using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class UmuServerEnvironmentTests
{
    [Fact]
    public void ServersHaveIndependentPrefixesAndSharedToolsWithinChosenDependencies()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-compatibility-planning");
        var first = UmuServerEnvironment.Build(root, Guid.NewGuid());
        var second = UmuServerEnvironment.Build(root, Guid.NewGuid());
        Assert.NotEqual(first["WINEPREFIX"], second["WINEPREFIX"]);
        Assert.Equal(first["UMU_FOLDERS_PATH"], second["UMU_FOLDERS_PATH"]);
        Assert.Equal(first["XDG_CACHE_HOME"], second["XDG_CACHE_HOME"]);
        foreach (var key in new[] { "WINEPREFIX", "UMU_FOLDERS_PATH", "XDG_CACHE_HOME" })
            Assert.StartsWith(Path.Combine(root, "Dependencies", "Compatibility") + Path.DirectorySeparatorChar, first[key]);
        Assert.Equal("GE-Proton", first["PROTONPATH"]);
        Assert.Null(first["UMU_NO_RUNTIME"]);
        Assert.Equal("waitforexitandrun", first["PROTON_VERB"]);
    }
    [Fact]
    public void UnregisteredServerOrRelativeStorageIsRefused()
    {
        Assert.Throws<ArgumentException>(() => UmuServerEnvironment.Build(Path.GetTempPath(), Guid.Empty));
        Assert.Throws<ArgumentException>(() => UmuServerEnvironment.Build("relative", Guid.NewGuid()));
    }
}
