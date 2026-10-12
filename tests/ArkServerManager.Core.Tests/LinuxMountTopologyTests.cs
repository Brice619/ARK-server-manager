using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class LinuxMountTopologyTests
{
    private const string Mounts = "20 1 8:1 / / rw - ext4 /dev/sda1 rw\n"
        + "21 20 0:30 /@ark /srv/ark rw shared:2 - btrfs /dev/sdb1 rw\n"
        + "22 21 8:2 / /srv/ark/Servers/one/Saved/SaveGames rw - ext4 /dev/sdc1 rw\n";

    [Fact]
    public void NestedMountIsNotMistakenForParentStorage()
    {
        var mounts = LinuxMountTopology.Parse(Mounts);
        Assert.Equal(22, LinuxMountTopology.Resolve("/srv/ark/Servers/one/Saved/SaveGames/player.sav", mounts).Id);
        Assert.Throws<NotSupportedException>(() => LinuxMountTopology.RequireSingleBtrfsMount(
            ["/srv/ark/Servers/one/Saved"], "/srv/ark/Dependencies/Snapshots", mounts));
        Assert.Throws<NotSupportedException>(() => LinuxMountTopology.RequireSingleBtrfsMount(
            ["/srv/ark/Servers/one/Saved/SaveGames"], "/srv/ark/Dependencies/Snapshots", mounts));
    }

    [Fact]
    public void WorldsAndTransfersResolveTogetherWithoutConfusingSimilarDirectoryNames()
    {
        var mounts = LinuxMountTopology.Parse(Mounts);
        var selected = LinuxMountTopology.RequireSingleBtrfsMount(
            ["/srv/ark/Servers/two/Saved", "/srv/ark/Clusters/one/Transfers"], "/srv/ark/Dependencies/Snapshots", mounts);
        Assert.Equal(21, selected.Id);
        Assert.Equal("/@ark", selected.Root);
        Assert.Equal(20, LinuxMountTopology.Resolve("/srv/ark-old/Servers/Saved", mounts).Id);
    }

    [Fact]
    public void EscapedPathsAndBindMountsPreserveTheirActualMountIdentities()
    {
        var mounts = LinuxMountTopology.Parse("30 1 0:40 /@home /home/my\\040servers rw - btrfs /dev/sdb1 rw\n"
            + "31 30 0:40 /@home/Servers /imported rw - btrfs /dev/sdb1 rw\n");
        Assert.Equal("/home/my servers", mounts[0].Directory);
        Assert.Throws<NotSupportedException>(() => LinuxMountTopology.RequireSingleBtrfsMount(
            ["/imported/one/Saved"], "/home/my servers/Dependencies/Snapshots", mounts));
        Assert.Throws<InvalidDataException>(() => LinuxMountTopology.Resolve("/imported/../private", mounts));
    }

    [Fact]
    public void UnsafeStorageAndMalformedMountInformationAreRefused()
    {
        var mounts = LinuxMountTopology.Parse(Mounts);
        Assert.Throws<ArgumentException>(() => LinuxMountTopology.RequireSingleBtrfsMount(
            ["/srv/ark/Servers/two/Saved"], "/srv/ark/Servers/two/Saved/Snapshots", mounts));
        Assert.Throws<InvalidDataException>(() => LinuxMountTopology.Parse(Mounts + "21 1 0:1 / /another rw - btrfs /dev/sdb1 rw\n"));
        Assert.Throws<InvalidDataException>(() => LinuxMountTopology.Parse("30 1 0:40 / /bad\\999path rw - btrfs /dev/sdb1 rw\n"));
    }
}
