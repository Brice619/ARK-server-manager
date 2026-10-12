using System.ComponentModel;
using System.Diagnostics;

namespace ArkServerManager.Core;

public sealed record ServerProcessCandidate(int ProcessId, DateTimeOffset StartedAt, string Executable,
    string Directory, ArkEdition Edition, string Platform)
{
    public Guid? LinuxBootId { get; init; }
    public long? LinuxStartedTicks { get; init; }
    public ulong? MappedInode { get; init; }
    public string? MappedDevice { get; init; }
}
public sealed record UnresolvedProcess(int ProcessId, string? Name, string Reason);
public enum ProcessIdentityState { Matches, NoLongerMatches, Unverified }
public sealed record ProcessIdentityCheck(ProcessIdentityState State, string? Finding);
public sealed record ServerProcessScan(DateTimeOffset MeasuredAt, IReadOnlyList<ServerProcessCandidate> Candidates,
    IReadOnlyList<UnresolvedProcess> UnresolvedProcesses, IReadOnlyList<string> Findings)
{
    public bool NativeScanComplete => UnresolvedProcesses.Count == 0;
}

/// <summary>Read-only native process identity discovery. A matching process is not proof of server health or ownership.</summary>
public sealed class ServerProcessDiscovery
{
    public Task<ServerProcessScan> ScanAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var candidates = new List<ServerProcessCandidate>();
        var unresolved = new List<UnresolvedProcess>();
        var findings = new List<string>();
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or IOException)
        { return new ServerProcessScan(DateTimeOffset.UtcNow, [], [new(0, null, error.Message)], ["The process list could not be inspected."]); }
        try
        {
            foreach (var process in processes)
            {
                ct.ThrowIfCancellationRequested();
                string? name = null;
                try
                {
                    name = process.ProcessName;
                    var serverName = LooksLikeServer(name);
                    var wineName = OperatingSystem.IsLinux() && name is "wine" or "wine64" or "wine-preloader" or "wine64-preloader";
                    if (!serverName && !wineName) continue;
                    if (process.HasExited) continue;
                    var started = new DateTimeOffset(process.StartTime.ToUniversalTime());
                    var executable = OperatingSystem.IsWindows() ? WindowsNativePath.LoadedProcessExecutable(process.Id) : process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executable))
                    { unresolved.Add(new(process.Id, name, "The executable path is unavailable.")); continue; }
                    var match = Match(process.Id, started, executable);
                    if (match is not null && !process.HasExited) candidates.Add(match);
                    else if (OperatingSystem.IsLinux() && !process.HasExited)
                    {
                        var observed = LinuxWineServerObserver.ReadAsync(process.Id, ct).GetAwaiter().GetResult();
                        if (observed is null) continue;
                        if (observed.Payloads.Count != 1 || observed.Payloads[0].Deleted)
                        { unresolved.Add(new(process.Id, name, "The compatibility process's loaded server file is missing, ambiguous or deleted.")); continue; }
                        var payload = observed.Payloads[0];
                        if (!process.HasExited) candidates.Add(new(process.Id, started, payload.Executable, payload.Directory, payload.Edition, "WindowsCompatibility")
                        { LinuxBootId = observed.Identity.BootId, LinuxStartedTicks = observed.Identity.StartedTicks, MappedInode = payload.Inode, MappedDevice = payload.Device });
                    }
                    else if (!process.HasExited)
                        unresolved.Add(new(process.Id, name, "The process name matches, but its executable layout could not be verified."));
                }
                catch (InvalidOperationException) { /* Process exited during inspection. */ }
                catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or IOException)
                {
                    // The process list is a snapshot. Confirm an exit before classifying a failed inspection as unresolved.
                    if (!ConfirmedExited(process)) unresolved.Add(new(process.Id, name, error.Message));
                }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        if (unresolved.Count > 0) findings.Add("Some processes could not be verified. An empty candidate list does not establish that all servers are stopped.");
        return new ServerProcessScan(DateTimeOffset.UtcNow, candidates, unresolved, findings);
    }, ct);

    private static bool ConfirmedExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>False can mean unverified; it must never be used alone as proof that an installation is stopped.</summary>
    public bool StillMatches(ServerProcessCandidate candidate) => CheckIdentity(candidate).State == ProcessIdentityState.Matches;

    public ProcessIdentityCheck CheckIdentity(ServerProcessCandidate candidate)
    {
        if (candidate.ProcessId < 1 || candidate.StartedAt == default) throw new ArgumentException("A valid process identity is required.");
        var expected = Path.GetFullPath(candidate.Executable);
        try
        {
            using var process = Process.GetProcessById(candidate.ProcessId);
            if (process.HasExited || new DateTimeOffset(process.StartTime.ToUniversalTime()) != candidate.StartedAt)
                return new(ProcessIdentityState.NoLongerMatches, "The observed process exited or its identity changed.");
            if (OperatingSystem.IsLinux() && candidate.Platform == "WindowsCompatibility")
            {
                var observed = LinuxWineServerObserver.ReadAsync(candidate.ProcessId).GetAwaiter().GetResult();
                if (observed is null) return new(ProcessIdentityState.NoLongerMatches, "The compatibility process exited.");
                if (candidate.LinuxBootId is not Guid boot || candidate.LinuxStartedTicks is not long ticks || candidate.MappedInode is null || candidate.MappedDevice is null)
                    return new(ProcessIdentityState.Unverified, "The compatibility process observation lacks kernel file identity.");
                return observed.Identity.Matches(candidate.ProcessId, boot, ticks) && observed.Payloads.Count == 1
                    && observed.Payloads[0] is { Deleted: false } payload && payload.Executable == candidate.Executable
                    && payload.Inode == candidate.MappedInode && payload.Device == candidate.MappedDevice
                    ? new(ProcessIdentityState.Matches, null) : new(ProcessIdentityState.Unverified, "The loaded compatibility server file changed.");
            }
            expected = WindowsNativePath.ResolveFinalExisting(expected);
            var executable = OperatingSystem.IsWindows() ? WindowsNativePath.LoadedProcessExecutable(process.Id) : process.MainModule?.FileName;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (executable is null) return new(ProcessIdentityState.Unverified, "The executable path is unavailable.");
            return WindowsNativePath.ResolveFinalExisting(executable).Equals(expected, comparison) && !process.HasExited
                ? new(ProcessIdentityState.Matches, null)
                : new(ProcessIdentityState.NoLongerMatches, "The observed executable changed or exited.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return new(ProcessIdentityState.NoLongerMatches, "The observed process is no longer available."); }
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or IOException)
        { return new(ProcessIdentityState.Unverified, error.Message); }
    }

    private static bool LooksLikeServer(string name) => InstallationDiscovery.Executables.Any(layout =>
    {
        var expected = Path.GetFileNameWithoutExtension(layout.Path);
        return name.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || OperatingSystem.IsLinux() && name.Equals(expected[..Math.Min(15, expected.Length)], StringComparison.OrdinalIgnoreCase);
    });

    private static ServerProcessCandidate? Match(int id, DateTimeOffset started, string executable)
    {
        var full = WindowsNativePath.ResolveFinalExisting(executable);
        foreach (var layout in InstallationDiscovery.Executables)
        {
            // This scanner does not interpret Wine/Proton wrappers as native Windows processes.
            if ((layout.Platform == "Windows") != OperatingSystem.IsWindows()) continue;
            var relative = layout.Path.Replace('/', Path.DirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.EndsWith(Path.DirectorySeparatorChar + relative, comparison)) continue;
            var directory = Path.TrimEndingDirectorySeparator(full[..^relative.Length]);
            return new(id, started, full, directory, layout.Edition, layout.Platform);
        }
        return null;
    }
}
