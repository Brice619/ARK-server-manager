using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ArkServerManager.Core;

public sealed record MemoryCapacity(long TotalBytes, long AvailableBytes);
public sealed record DiskCapacity(string QueriedFolder, long TotalBytes, long AvailableBytes);
public sealed record ResourceSnapshot(DateTimeOffset MeasuredAt, MemoryCapacity? Memory, DiskCapacity? Disk,
    string? MemoryError, string? DiskError);
public sealed record ResourceRequirements(long? MemoryBytes, long? DiskBytes);
public enum ResourceAvailability { Sufficient, Insufficient, Unknown }
public sealed record ResourceFinding(string Resource, ResourceAvailability Availability, long? RequiredBytes, long? AvailableBytes);
public sealed record ResourceAssessment(ResourceFinding Memory, ResourceFinding Disk)
{
    public bool HasInsufficientResources => Memory.Availability == ResourceAvailability.Insufficient || Disk.Availability == ResourceAvailability.Insufficient;
    public bool HasUnknownResources => Memory.Availability == ResourceAvailability.Unknown || Disk.Availability == ResourceAvailability.Unknown;
}

/// <summary>Read-only capacity measurements; required server estimates and user warning decisions are separate.</summary>
public static class ResourceChecks
{
    public static ResourceSnapshot Measure(string destinationFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        var requested = Path.GetFullPath(destinationFolder);
        MemoryCapacity? memory = null;
        DiskCapacity? disk = null;
        string? memoryError = null, diskError = null;
        try
        {
            if (OperatingSystem.IsWindows()) memory = ReadWindowsMemory();
            else if (OperatingSystem.IsLinux()) memory = ReadLinuxMemory(File.ReadAllText("/proc/meminfo"));
            else memoryError = "Memory measurement is unavailable on this operating system.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or OverflowException)
        { memoryError = error.Message; }
        try
        {
            var existing = ExistingAncestor(requested);
            if (OperatingSystem.IsWindows())
            {
                if (!GetDiskFreeSpaceEx(existing + Path.DirectorySeparatorChar, out var available, out var total, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                disk = new(existing, checked((long)total), checked((long)available));
            }
            else
            {
                // Match mount paths by whole segments, never confuse /data with /database.
                // Links cannot be resolved by lexical mount matching and must be reported as unknown.
                for (var ancestor = new DirectoryInfo(existing); ancestor is not null; ancestor = ancestor.Parent)
                    if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Disk capacity for this linked folder could not be evaluated.");
                var drive = DriveInfo.GetDrives().Where(d => Contains(d.Name, existing))
                    .OrderByDescending(d => d.Name.Length).FirstOrDefault()
                    ?? throw new IOException("The destination filesystem could not be identified.");
                disk = new(existing, drive.TotalSize, drive.AvailableFreeSpace);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or OverflowException)
        { diskError = error.Message; }
        return new(DateTimeOffset.UtcNow, memory, disk, memoryError, diskError);
    }

    public static ResourceAssessment Assess(ResourceSnapshot snapshot, ResourceRequirements requirements)
    {
        if (requirements.MemoryBytes < 0 || requirements.DiskBytes < 0) throw new ArgumentException("Resource requirements cannot be negative.");
        static ResourceFinding Compare(string resource, long? required, long? available) => new(resource,
            required is null || available is null ? ResourceAvailability.Unknown
                : available < required ? ResourceAvailability.Insufficient : ResourceAvailability.Sufficient,
            required, available);
        return new(Compare("Memory", requirements.MemoryBytes, snapshot.Memory?.AvailableBytes),
            Compare("Disk", requirements.DiskBytes, snapshot.Disk?.AvailableBytes));
    }

    public static MemoryCapacity ReadLinuxMemory(string meminfo)
    {
        long? total = null, available = null;
        foreach (var line in meminfo.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || fields[0] is not ("MemTotal:" or "MemAvailable:")) continue;
            if (fields.Length != 3 || fields[2] != "kB" || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kibibytes))
                throw new InvalidDataException("Linux memory statistics are invalid.");
            long bytes;
            try { bytes = checked(kibibytes * 1024); }
            catch (OverflowException error) { throw new InvalidDataException("Linux memory statistics exceed supported values.", error); }
            if (fields[0] == "MemTotal:")
            {
                if (total.HasValue) throw new InvalidDataException("Linux memory statistics contain duplicate totals.");
                total = bytes;
            }
            else
            {
                if (available.HasValue) throw new InvalidDataException("Linux memory statistics contain duplicate available values.");
                available = bytes;
            }
        }
        if (total is null or <= 0 || available is null || available > total)
            throw new InvalidDataException("Linux memory capacity could not be determined.");
        return new(total.Value, available.Value);
    }

    private static MemoryCapacity ReadWindowsMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(checked((long)status.TotalPhysical), checked((long)status.AvailablePhysical));
    }

    private static string ExistingAncestor(string path)
    {
        while (true)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) == 0) throw new IOException("The destination or one of its parents is a file.");
                return path;
            }
            catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException)
            {
                path = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))
                    ?? throw new DirectoryNotFoundException("The destination has no accessible filesystem root.");
            }
        }
    }
    private static bool Contains(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        return path == root || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
}
