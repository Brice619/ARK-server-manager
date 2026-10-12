namespace ArkServerManager.Core;
public static class LinuxSteamCmdRuntimePlan
{
    public static LinuxPackageCommand Select(string osRelease)
    {
        var distribution = LinuxBackupToolPlan.Select(osRelease);
        return distribution.Executable switch
        {
            "/usr/bin/apt-get" => new(distribution.Executable, ["install", "--yes", "--no-install-recommends", "lib32gcc-s1", "lib32stdc++6"]),
            "/usr/bin/dnf" => new(distribution.Executable, ["install", "--assumeyes", "glibc.i686", "libgcc.i686", "libstdc++.i686"]),
            "/usr/bin/pacman" => new(distribution.Executable, ["--sync", "--needed", "--noconfirm", "lib32-glibc", "lib32-gcc-libs"]),
            _ => throw new NotSupportedException("SteamCMD's Linux runtime installer is unavailable.")
        };
    }
    public static bool IsElf32(ReadOnlySpan<byte> header) => header.Length >= 20
        && header[0] == 0x7f && header[1] == 'E' && header[2] == 'L' && header[3] == 'F'
        && header[4] == 1 && header[5] == 1 && header[18] == 3 && header[19] == 0;
    public static bool SupportedLoaderVersion(string text) => text.TrimStart().StartsWith("ld.so", StringComparison.OrdinalIgnoreCase);
    public static bool LibrariesPresent()
    {
        var roots = new[] { "/lib32", "/usr/lib32", "/lib/i386-linux-gnu", "/usr/lib/i386-linux-gnu", "/lib", "/usr/lib" };
        return new[] { "libgcc_s.so.1", "libstdc++.so.6" }.All(name => roots.Any(root => ValidFile(Path.Combine(root, name))));
    }
    public static bool ValidFile(string path)
    {
        try { using var stream = File.OpenRead(path); Span<byte> header = stackalloc byte[20]; return stream.Read(header) == header.Length && IsElf32(header); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
