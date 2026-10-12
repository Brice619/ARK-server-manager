using System.Runtime.InteropServices;
namespace ArkServerManager.Core;

public static class LinuxNotificationToolPlan
{
    public static bool LibrariesPresent()
    {
        foreach (var name in new[] { "libnotify.so.4", "libgobject-2.0.so.0", "libglib-2.0.so.0" })
        {
            if (!NativeLibrary.TryLoad(name, out var handle)) return false;
            NativeLibrary.Free(handle);
        }
        return true;
    }
    public static LinuxPackageCommand Select(string osRelease)
    {
        var distribution = LinuxBackupToolPlan.Select(osRelease);
        return distribution.Executable switch
        {
            "/usr/bin/apt-get" => new(distribution.Executable, ["install", "--yes", "--no-install-recommends", "libnotify4"]),
            "/usr/bin/dnf" => new(distribution.Executable, ["install", "--assumeyes", "libnotify"]),
            "/usr/bin/pacman" => new(distribution.Executable, ["--sync", "--needed", "--noconfirm", "libnotify"]),
            _ => throw new NotSupportedException("The Linux notification installer is unavailable.")
        };
    }
}
