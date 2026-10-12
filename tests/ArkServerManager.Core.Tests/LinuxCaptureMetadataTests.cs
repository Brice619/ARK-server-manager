using System.Buffers.Binary;
using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class LinuxCaptureMetadataTests
{
    private static byte[] Metadata()
    {
        var bytes = new byte[256]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x7ff);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 0x81a4); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 123);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), 100); return bytes;
    }
    [Fact]
    public void IdentityDetectsChangeTimeEvenWhenLengthAndModificationTimeMatch()
    {
        var data = Metadata(); var before = LinuxCaptureMetadata.Parse(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(104), 1);
        var after = LinuxCaptureMetadata.Parse(data);
        Assert.Equal(before.Bytes, after.Bytes); Assert.NotEqual(before.Revision, after.Revision);
        Assert.Throws<IOException>(() => StableCaptureInventory.RequireUnchanged([new("world.ark", false, before.Bytes, before.Revision)],
            [new("world.ark", false, after.Bytes, after.Revision)]));
    }
    [Fact]
    public void MissingMetadataAndLinksAreRefused()
    {
        Assert.Throws<InvalidDataException>(() => LinuxCaptureMetadata.Parse(new byte[256]));
        var data = Metadata(); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 0xa1ff);
        Assert.Throws<IOException>(() => LinuxCaptureMetadata.Parse(data));
    }
    [Fact]
    public void OpenedFileMustMatchReviewedIdentity()
    {
        var original = LinuxCaptureMetadata.Parse(Metadata());
        LinuxCaptureFile.RequireIdentity(original, original);
        Assert.Throws<IOException>(() => LinuxCaptureFile.RequireIdentity(original, original with { Revision = "replacement" }));
        Assert.Throws<IOException>(() => LinuxCaptureFile.RequireIdentity(original, original with { Bytes = original.Bytes + 1 }));
        Assert.Throws<IOException>(() => LinuxCaptureFile.RequireIdentity(original, original with { IsDirectory = true }));
    }
}
