using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;

public sealed class LinuxBackupCapturePlanTests
{
    [Theory]
    [InlineData("ext4", LinuxBackupCaptureKind.VerifiedCopy)]
    [InlineData("xfs", LinuxBackupCaptureKind.VerifiedCopy)]
    [InlineData("btrfs", LinuxBackupCaptureKind.BtrfsSnapshot)]
    public void SelectsCaptureForLocalStorage(string fileSystem, LinuxBackupCaptureKind expected)
    {
        Assert.Equal(expected, LinuxBackupCapturePlan.Select(["/app/Servers/one/ShooterGame/Saved", "/app/Clusters/main/Transfers"],
            "/app/Dependencies/LinuxSnapshots", "/app/Dependencies/LinuxCaptures", [new(1, 0, "8:1", "/", "/", fileSystem, "/dev/root")]));
    }
    [Fact]
    public void VerifiedCopyCanCoordinateSeparateLocalMounts()
    {
        Assert.Equal(LinuxBackupCaptureKind.VerifiedCopy, LinuxBackupCapturePlan.Select(["/worlds/one", "/transfers/main"],
            "/app/snapshots", "/app/captures", [new(1, 0, "8:1", "/", "/", "ext4", "/dev/root"),
                new(2, 1, "8:2", "/", "/worlds", "xfs", "/dev/worlds")]));
    }
    [Fact]
    public void RemoteAndNestedStorageAreRefused()
    {
        var local = new LinuxMount(1, 0, "8:1", "/", "/", "ext4", "/dev/root");
        Assert.Throws<NotSupportedException>(() => LinuxBackupCapturePlan.Select(["/world"], "/snapshots", "/captures",
            [local with { FileSystem = "nfs" }]));
        Assert.Throws<NotSupportedException>(() => LinuxBackupCapturePlan.Select(["/world"], "/snapshots", "/captures",
            [local, new(2, 1, "8:2", "/", "/world/nested", "ext4", "/dev/other")]));
    }
    [Fact]
    public void CaptureStorageCannotOverlapSources()
    {
        Assert.Throws<ArgumentException>(() => LinuxBackupCapturePlan.Select(["/world"], "/snapshots", "/world/captures",
            [new(1, 0, "8:1", "/", "/", "ext4", "/dev/root")]));
    }
}
