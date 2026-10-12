using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ArkServerManager.Core;

/// <summary>Copies a regular file from its opened identity; callers must also verify the complete source inventory.</summary>
public static class LinuxCaptureFile
{
    [SupportedOSPlatform("linux")]
    public static async Task CopyAsync(string source, string destination, LinuxCaptureFileIdentity expected, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (expected.IsDirectory) throw new ArgumentException("A regular saved-state file is required.", nameof(expected));
        // NONBLOCK prevents a replacement FIFO from hanging open; NOFOLLOW refuses a replacement symlink.
        var descriptor = Open(source, 0x800 | 0x20000 | 0x80000);
        if (descriptor < 0) throw new IOException("Saved-state file could not be opened safely.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        RequireIdentity(expected, LinuxCaptureMetadata.Read(handle));
        using var input = new FileStream(handle, FileAccess.Read);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[128 * 1024];
        var remaining = expected.Bytes;
        while (remaining > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (count == 0) throw new IOException("Saved-state file shortened during capture.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            remaining -= count;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), ct) != 0) throw new IOException("Saved-state file grew during capture.");
        RequireIdentity(expected, LinuxCaptureMetadata.Read(handle));
        RequireIdentity(expected, LinuxCaptureMetadata.Read(source));
        await output.FlushAsync(ct);
    }

    public static void RequireIdentity(LinuxCaptureFileIdentity expected, LinuxCaptureFileIdentity actual)
    {
        if (expected.IsDirectory || actual.IsDirectory || expected != actual)
            throw new IOException("Saved-state file was changed or replaced during capture. Discard this capture and retry.");
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
