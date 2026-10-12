namespace ArkServerManager.Core;

public static class LinuxCompatibilityToolPlan
{
    public static LinuxPackageCommand SelectPythonInstall(string osRelease)
    {
        var distribution = LinuxBackupToolPlan.Select(osRelease);
        return distribution.Executable switch
        {
            "/usr/bin/apt-get" => new(distribution.Executable, ["install", "--yes", "--no-install-recommends", "python3", "ca-certificates"]),
            "/usr/bin/dnf" => new(distribution.Executable, ["install", "--assumeyes", "python3", "ca-certificates"]),
            "/usr/bin/pacman" => new(distribution.Executable, ["--sync", "--needed", "--noconfirm", "python", "ca-certificates"]),
            _ => throw new NotSupportedException("The Linux Python installer is unavailable.")
        };
    }
    public static bool SupportedPython(string version)
    {
        var text = version.Trim();
        return text.StartsWith("Python ", StringComparison.Ordinal)
            && Version.TryParse(text[7..], out var parsed) && parsed >= new Version(3, 10);
    }
}
