using System.ComponentModel;
using System.Diagnostics;

namespace ArkServerManager.Core;

public sealed record SteamCmdProcessCandidate(int ProcessId, string Executable);
public sealed record SteamCmdProcessScan(DateTimeOffset MeasuredAt, IReadOnlyList<SteamCmdProcessCandidate> Processes,
    int UnresolvedProcesses);

/// <summary>Read-only native SteamCMD detection, including Linux's script-launched linux32 binary.</summary>
public sealed class SteamCmdProcessDiscovery
{
    public Task<SteamCmdProcessScan> ScanAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var found = new List<SteamCmdProcessCandidate>(); var unresolved = 0;
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!process.ProcessName.StartsWith("steamcmd", StringComparison.OrdinalIgnoreCase)) continue;
                    if (process.HasExited) continue;
                    var executable = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executable)) unresolved++;
                    else if (!process.HasExited) found.Add(new(process.Id, Path.GetFullPath(executable)));
                }
                catch (InvalidOperationException) { /* Exited during inspection. */ }
                catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or IOException) { unresolved++; }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return new SteamCmdProcessScan(DateTimeOffset.UtcNow, found, unresolved);
    }, ct);
}

public sealed class ServerInstallationResumeGuard(string steamCmdExecutable,
    Func<CancellationToken, Task<SteamCmdProcessScan>> scanInstaller,
    Func<CancellationToken, Task<ServerProcessScan>> scanServers)
{
    public ServerInstallationResumeGuard(string steamCmdExecutable)
        : this(steamCmdExecutable, new SteamCmdProcessDiscovery().ScanAsync, new ServerProcessDiscovery().ScanAsync) { }

    public async Task<bool> VerifyAsync(ServerInstallationRecord record, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(steamCmdExecutable) || !File.Exists(steamCmdExecutable)
            || new FileInfo(steamCmdExecutable).Length == 0) return false;
        if ((record.Platform == ServerPlatform.Windows && !OperatingSystem.IsWindows())
            || (record.Platform == ServerPlatform.Linux && !OperatingSystem.IsLinux()) || !Enum.IsDefined(record.Platform)) return false;
        var toolRoot = Path.GetDirectoryName(Path.GetFullPath(steamCmdExecutable))!;
        foreach (var location in new[] { steamCmdExecutable, record.StagingDirectory, record.FinalDirectory })
        {
            if (!Path.IsPathFullyQualified(location)) return false;
            for (var path = Path.GetFullPath(location); path is not null; path = Path.GetDirectoryName(path))
                if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var started = DateTimeOffset.UtcNow;
        var installer = await scanInstaller(ct);
        if (installer.UnresolvedProcesses != 0 || installer.MeasuredAt < started || installer.MeasuredAt > DateTimeOffset.UtcNow
            || installer.Processes.Any(p => Path.GetFullPath(p.Executable).StartsWith(toolRoot + Path.DirectorySeparatorChar, comparison))) return false;
        started = DateTimeOffset.UtcNow;
        var servers = await scanServers(ct);
        if (!servers.NativeScanComplete || servers.MeasuredAt < started || servers.MeasuredAt > DateTimeOffset.UtcNow) return false;
        return !servers.Candidates.Any(p => new[] { record.StagingDirectory, record.FinalDirectory }.Any(location =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Directory)), comparison)));
    }
}
