namespace ArkServerManager.Core;

public static class SteamCmdProcessSafety
{
    public static void RequireIdle(string executable, SteamCmdProcessScan scan, DateTimeOffset started, DateTimeOffset finished)
    {
        if (!Path.IsPathFullyQualified(executable) || scan.UnresolvedProcesses != 0 || scan.MeasuredAt < started || scan.MeasuredAt > finished)
            throw new IOException("SteamCMD process state could not be verified. Review running installation tools before retrying.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(executable))!);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (scan.Processes.Any(process => !Path.IsPathFullyQualified(process.Executable) || process.ProcessId <= 0
            || Path.GetFullPath(process.Executable).StartsWith(root + Path.DirectorySeparatorChar, comparison)))
            throw new IOException("SteamCMD is still running from this tool folder. Wait for it to finish before starting another installation or repair.");
    }
}
