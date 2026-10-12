using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class WindowsVolumeSnapshotTests
{
    [Fact]
    public void ClusterMapsAndTransfersResolveIntoOneShadowView()
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = new[] { @"D:\Servers\Island\Saved", @"D:\Servers\Scorched\Saved", @"D:\Clusters\Transfers" };
        var mapped = WindowsVolumeSnapshot.ResolveDirectories(paths, @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12");
        Assert.Equal(3, mapped.Count);
        Assert.All(mapped.Values, path => Assert.StartsWith(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12\", path));
        Assert.EndsWith(@"Clusters\Transfers", mapped[paths[2]]);
    }

    [Fact]
    public void SeparateVolumesAndDuplicateSourcesRequireReviewBeforeCapture()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string device = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12";
        Assert.Throws<NotSupportedException>(() => WindowsVolumeSnapshot.ResolveDirectories(
            [@"D:\Servers\Saved", @"E:\Clusters\Transfers"], device));
        Assert.Throws<ArgumentException>(() => WindowsVolumeSnapshot.ResolveDirectories(
            [@"D:\Servers\Saved", @"d:\servers\saved\"], device));
    }

    [Fact]
    public void MapsOnlyStateDirectoryInsideShadowDevice()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = WindowsVolumeSnapshot.ResolveDirectory(@"D:\ARK Servers\Island\ShooterGame\Saved",
            @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12");
        Assert.Equal(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12\ARK Servers\Island\ShooterGame\Saved", result);
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12\..\Windows")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12\")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy0")]
    public void RejectsUnexpectedDevicePaths(string device)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<InvalidDataException>(() => WindowsVolumeSnapshot.ResolveDirectory(@"D:\Servers\Saved", device));
    }

    [Fact]
    public void RejectsVolumeRootAndNetworkSource()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string device = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12";
        Assert.Throws<InvalidDataException>(() => WindowsVolumeSnapshot.ResolveDirectory(@"D:\", device));
        Assert.Throws<NotSupportedException>(() => WindowsVolumeSnapshot.ResolveDirectory(@"\\host\share\Saved", device));
    }
}
