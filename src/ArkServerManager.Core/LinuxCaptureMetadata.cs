using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
namespace ArkServerManager.Core;
public sealed record LinuxCaptureFileIdentity(bool IsDirectory, long Bytes, string Revision);
public static class LinuxCaptureMetadata
{
    [SupportedOSPlatform("linux")]
    public static LinuxCaptureFileIdentity Read(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var buffer = new byte[256];
        // Inspect the link itself, avoid automount side effects and request fresh filesystem metadata.
        if (Statx(-100, path, 0x100 | 0x800 | 0x2000, 0x7ff, buffer) != 0)
            throw new IOException("Saved-state metadata could not be read.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        return Parse(buffer);
    }
    [SupportedOSPlatform("linux")]
    internal static LinuxCaptureFileIdentity Read(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        var held = false;
        try
        {
            handle.DangerousAddRef(ref held);
            if (Statx(handle.DangerousGetHandle().ToInt32(), "", 0x1000 | 0x2000, 0x7ff, buffer) != 0)
                throw new IOException("Open saved-state metadata could not be read.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            return Parse(buffer);
        }
        finally { if (held) handle.DangerousRelease(); }
    }
    public static LinuxCaptureFileIdentity Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length != 256 || (BinaryPrimitives.ReadUInt32LittleEndian(data) & 0x3c3) != 0x3c3)
            throw new InvalidDataException("Filesystem identity and change-time metadata are required for capture.");
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(data[28..]); var type = mode & 0xf000;
        if (type is not (0x4000 or 0x8000)) throw new IOException("Linked or special saved-state entries cannot be captured.");
        var length = BinaryPrimitives.ReadUInt64LittleEndian(data[40..]);
        if (length > long.MaxValue) throw new InvalidDataException("Saved-state file exceeds supported size.");
        var inode = BinaryPrimitives.ReadUInt64LittleEndian(data[32..]);
        var deviceMajor = BinaryPrimitives.ReadUInt32LittleEndian(data[136..]); var deviceMinor = BinaryPrimitives.ReadUInt32LittleEndian(data[140..]);
        var changedSeconds = BinaryPrimitives.ReadInt64LittleEndian(data[96..]); var changedNanos = BinaryPrimitives.ReadUInt32LittleEndian(data[104..]);
        var modifiedSeconds = BinaryPrimitives.ReadInt64LittleEndian(data[112..]); var modifiedNanos = BinaryPrimitives.ReadUInt32LittleEndian(data[120..]);
        if (changedNanos >= 1000000000 || modifiedNanos >= 1000000000) throw new InvalidDataException("Invalid saved-state timestamps.");
        return new(type == 0x4000, type == 0x4000 ? 0 : (long)length,
            $"{deviceMajor}:{deviceMinor}:{inode}:{mode}:{length}:{changedSeconds}:{changedNanos}:{modifiedSeconds}:{modifiedNanos}");
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] data);
}
