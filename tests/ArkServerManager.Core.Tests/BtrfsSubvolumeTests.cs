using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class BtrfsSubvolumeTests
{
    [Fact]
    public void SnapshotIdentityRequiresItsOriginalParentAndReadOnlyState()
    {
        var source = Guid.NewGuid(); var snapshot = Guid.NewGuid();
        var metadata = BtrfsSubvolume.Parse($"/srv/snapshot\n UUID: {snapshot}\n Parent UUID: {source}\n Subvolume ID: 300\n Flags: readonly\n Snapshot(s):\n");
        metadata.RequireSnapshotIdentity(source, snapshot);
        Assert.Throws<InvalidDataException>(() => metadata.RequireSnapshotIdentity(Guid.NewGuid(), snapshot));
        Assert.Throws<InvalidDataException>(() => metadata.RequireSnapshotIdentity(source, Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(() => (metadata with { ReadOnly = false }).RequireSnapshotIdentity(source, snapshot));
        Assert.Throws<InvalidDataException>(() => (metadata with { Uuid = source }).RequireSnapshotIdentity(source, null));
    }

    [Fact]
    public void AmbiguousOrIncompleteOwnershipMetadataIsRefused()
    {
        var uuid = Guid.NewGuid();
        var valid = $"UUID: {uuid}\nParent UUID: -\nSubvolume ID: 256\nFlags: -\n";
        Assert.Equal(uuid, BtrfsSubvolume.Parse(valid).Uuid);
        Assert.Throws<InvalidDataException>(() => BtrfsSubvolume.Parse(valid + $"UUID: {Guid.NewGuid()}\n"));
        Assert.Throws<InvalidDataException>(() => BtrfsSubvolume.Parse(valid.Replace("Parent UUID: -", "Parent UUID: invalid")));
        Assert.Throws<InvalidDataException>(() => BtrfsSubvolume.Parse(valid.Replace("Subvolume ID: 256", "Subvolume ID: 0")));
    }

    [Fact]
    public void NestedSubvolumePathsPreserveSpacesAndRejectTraversal()
    {
        var paths = BtrfsSubvolume.ListedFileSystemPaths("ID 256 gen 1 top level 5 path <FS_TREE>/@ark\n"
            + "ID 257 gen 1 top level 256 path @ark/Servers/My Map/Saved/SaveGames\n");
        Assert.Equal(new[] { "/@ark", "/@ark/Servers/My Map/Saved/SaveGames" }, paths);
        Assert.Throws<InvalidDataException>(() => BtrfsSubvolume.ListedFileSystemPaths("ID 258 gen 1 top level 256 path @ark/../private\n"));
        Assert.Throws<InvalidDataException>(() => BtrfsSubvolume.ListedFileSystemPaths("unexpected output\n"));
    }
}
