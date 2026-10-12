using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class StableCaptureInventoryTests
{
    [Fact]
    public void SameSizedFileReplacementAndDirectoryChangesInvalidateCapture()
    {
        StableCaptureEntry[] original = [new("", true, 0, "directory-inode-1-ctime-1"), new("world.ark", false, 100, "file-inode-2-ctime-1")];
        StableCaptureInventory.RequireUnchanged(original, original.Reverse().ToArray());
        Assert.Throws<IOException>(() => StableCaptureInventory.RequireUnchanged(original,
            [original[0], original[1] with { Revision = "file-inode-3-ctime-1" }]));
        Assert.Throws<IOException>(() => StableCaptureInventory.RequireUnchanged(original,
            [original[0] with { Revision = "directory-inode-1-ctime-2" }, original[1]]));
        Assert.Throws<IOException>(() => StableCaptureInventory.RequireUnchanged(original, [original[0]]));
    }
    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("nested\\file")]
    [InlineData("nested//file")]
    public void InvalidRelativeNamesAreRejected(string name)
        => Assert.Throws<InvalidDataException>(() => StableCaptureInventory.RequireUnchanged([new(name, false, 1, "revision")], []));
}
