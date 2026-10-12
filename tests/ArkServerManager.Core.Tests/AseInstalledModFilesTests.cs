using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class AseInstalledModFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-installed-mods-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData("missing")]
    [InlineData("empty-folder")]
    [InlineData("compressed-only")]
    [InlineData("empty-descriptor")]
    [InlineData("empty-asset")]
    [InlineData("uppercase-asset")]
    [InlineData("installed")]
    [InlineData("wrong-descriptor")]
    [InlineData("truncated-descriptor")]
    [InlineData("leading-zero-id")]
    public async Task DownloadCacheAndEmptyFoldersCannotCountAsInstalledMod(string state)
    {
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root, new()) { Mods = [state == "leading-zero-id" ? "02182894352" : "2182894352"] };
        var mods = Path.Combine(root, "ShooterGame", "Content", "Mods");
        var folder = Path.Combine(mods, "2182894352"); Directory.CreateDirectory(folder);
        var descriptor = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(descriptor, state == "wrong-descriptor" ? 123UL : 2182894352UL);
        if (state != "missing") await File.WriteAllBytesAsync(Path.Combine(mods, "2182894352.mod"), state == "empty-descriptor" ? [] : state == "truncated-descriptor" ? descriptor[..7] : descriptor);
        if (state == "compressed-only") await File.WriteAllTextAsync(Path.Combine(folder, "PrimalGameData.uasset.z"), "downloaded compressed asset");
        if (state is "installed" or "empty-descriptor" or "wrong-descriptor" or "truncated-descriptor" or "leading-zero-id") await File.WriteAllTextAsync(Path.Combine(folder, "PrimalGameData.uasset"), "unpacked asset");
        if (state == "empty-asset") await File.WriteAllTextAsync(Path.Combine(folder, "PrimalGameData.uasset"), "");
        if (state == "uppercase-asset") await File.WriteAllTextAsync(Path.Combine(folder, "PrimalGameData.UASSET"), "unpacked asset");
        if (state is "installed" or "uppercase-asset" or "leading-zero-id") await AseInstalledModFiles.RequireAsync(server);
        else await Assert.ThrowsAsync<IOException>(() => AseInstalledModFiles.RequireAsync(server));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Fact]
    public async Task AValidAssetDoesNotHideALinkedSubfolder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(root, "ShooterGame", "Content", "Mods", "2182894352");
        var outside = Path.Combine(root, "outside"); var linked = Path.Combine(folder, "linked");
        Directory.CreateDirectory(folder); Directory.CreateDirectory(outside);
        var descriptor = new byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(descriptor, 2182894352UL);
        await File.WriteAllBytesAsync(folder + ".mod", descriptor);
        await File.WriteAllTextAsync(Path.Combine(folder, "Valid.uasset"), "asset");
        var preserved = Path.Combine(outside, "preserved.uasset"); await File.WriteAllTextAsync(preserved, "keep");
        using var link = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { Arguments = $"/c mklink /J \"{linked}\" \"{outside}\"", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true })!;
        await link.WaitForExitAsync(); Assert.Equal(0, link.ExitCode);
        try
        {
            var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", root, new()) { Mods = ["2182894352"] };
            await Assert.ThrowsAsync<IOException>(() => AseInstalledModFiles.RequireAsync(server));
            Assert.Equal("keep", await File.ReadAllTextAsync(preserved));
        }
        finally { Directory.Delete(linked); }
    }
}
