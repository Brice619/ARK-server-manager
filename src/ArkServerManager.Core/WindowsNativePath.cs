using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ArkServerManager.Core;

public static class WindowsNativePath
{
    public static string LoadedProcessExecutable(int processId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid) throw new IOException("The loaded server process image could not be inspected.");
        var image = new StringBuilder(32768); var size = (uint)image.Capacity;
        if (!QueryFullProcessImageName(process, 1, image, ref size))
            throw new IOException("The loaded server process image is unavailable.");
        var native = image.ToString();
        if (native.StartsWith("\\Device\\Mup\\", StringComparison.OrdinalIgnoreCase))
            return ResolveFinalExisting("\\\\" + native[12..]);
        var devices = new NativeWindowsLaunchDriveBackend();
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var targets = devices.Query(letter);
            if (targets.Count == 0) continue;
            var target = targets[0].TrimEnd('\\');
            if (!target.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase)
                || !native.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            return ResolveFinalExisting(letter + ":" + native[target.Length..]);
        }
        throw new IOException("The loaded server image's physical volume could not be verified.");
    }
    public static string ResolveFinalExisting(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()) return full;
        var source = full.StartsWith("\\\\?\\", StringComparison.Ordinal) ? full
            : full.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + full[2..] : "\\\\?\\" + full;
        using var handle = CreateFile(source, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("The existing native file identity could not be opened.");
        var buffer = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw new IOException("The native file's final path could not be verified.");
        var result = buffer.ToString();
        if (result.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) result = "\\\\" + result[8..];
        else if (result.StartsWith("\\\\?\\", StringComparison.Ordinal)) result = result[4..];
        return Path.GetFullPath(result);
    }
    public static string ExpandExisting(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()) return full;
        return ConvertPath(full, GetLongPathName);
    }

    /// <summary>Returns an existing folder's shorter Windows name, never creating an alias or moving files.</summary>
    public static string ShortenExistingDirectory(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!OperatingSystem.IsWindows()) return full;
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("The native launch folder is missing.");
        var shorter = ConvertPath(full, GetShortPathName);
        if (!ExpandExisting(shorter).Equals(ExpandExisting(full), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The shorter native launch folder could not be verified.");
        return shorter.Length < full.Length ? shorter : full;
    }

    private delegate uint ConvertNative(string source, StringBuilder destination, uint capacity);
    private static string ConvertPath(string path, ConvertNative convert)
    {
        var source = path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path
            : path.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + path[2..] : "\\\\?\\" + path;
        var buffer = new StringBuilder(32768);
        var count = convert(source, buffer, (uint)buffer.Capacity);
        if (count == 0 || count >= buffer.Capacity) throw new IOException("The existing native path could not be resolved.");
        var result = buffer.ToString();
        if (result.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) result = "\\\\" + result[8..];
        else if (result.StartsWith("\\\\?\\", StringComparison.Ordinal)) result = result[4..];
        return Path.GetFullPath(result);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string source, StringBuilder destination, uint capacity);
    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string source, StringBuilder destination, uint capacity);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder destination, uint capacity, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
}
