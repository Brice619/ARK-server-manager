using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ArkServerManager.Core;

public sealed record LinuxBackupToolSetupResult(bool Ready, bool InstalledNow, string? Executable, string Notice);
public sealed record LinuxPackageInstallRecord(int SchemaVersion, Guid OperationId, int? ProcessId, long? StartedTicks, bool Completed)
{
    public Guid BootId { get; init; }
}

/// <summary>Checks existing tools first and retains package-process identity across host restarts.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxBackupToolSetup(string managementRoot)
{
    public Task<LinuxBackupToolSetupResult> EnsureAsync(Action<OperationProgress> progress, CancellationToken ct)
        => new LinuxRequiredToolSetup(managementRoot).EnsureAsync(progress, ct);
}

[SupportedOSPlatform("linux")]
public sealed class LinuxRequiredToolSetup(string managementRoot, bool compatibility = false, bool steamCmd = false, bool notifications = false)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = Gates.GetOrAdd(Path.GetFullPath(managementRoot), _ => new(1, 1));
    private string RecordPath => Path.Combine(managementRoot, "app-settings", notifications ? "linux-notification-tools.json" : steamCmd ? "linux-steamcmd-runtime.json" : compatibility ? "linux-compatibility-tools.json" : "linux-backup-tools.json");

    public async Task<LinuxBackupToolSetupResult> EnsureAsync(Action<OperationProgress> progress, CancellationToken ct)
    {
        if (new[] { compatibility, steamCmd, notifications }.Count(value => value) > 1) throw new ArgumentException("Choose one Linux tool installation kind.");
        await gate.WaitAsync(ct);
        try
        {
            var store = new AtomicJsonStore<LinuxPackageInstallRecord>(RecordPath);
            var previous = await store.LoadAsync(ct);
            if (previous is not null)
            {
                if (previous.SchemaVersion is not (1 or 2) || previous.OperationId == Guid.Empty
                    || previous.ProcessId is <= 0 || previous.StartedTicks is < 0
                    || previous.ProcessId.HasValue != previous.StartedTicks.HasValue
                    || previous.SchemaVersion == 2 && previous.ProcessId.HasValue && previous.BootId == Guid.Empty)
                    throw new InvalidDataException("Review the recorded Linux tool installation before retrying.");
                if (!previous.Completed && previous.ProcessId is int id)
                {
                    var process = await LinuxProcessIdentity.ReadAsync(id, ct);
                    if (process?.Alive == true && previous.SchemaVersion == 1)
                        return new(false, false, null, "A previous installer process still exists; its older identity needs review before retrying.");
                    if (process?.Matches(id, previous.BootId, previous.StartedTicks!.Value) == true)
                        return new(false, false, null, "The previous tool installation is still running. Check it again later.");
                }
            }
            if (!compatibility && !steamCmd && !notifications)
            {
                var topology = LinuxMountTopology.Parse(await File.ReadAllTextAsync("/proc/self/mountinfo", ct));
                var fileSystem = LinuxMountTopology.Resolve(Path.TrimEndingDirectorySeparator(Path.GetFullPath(managementRoot)), topology).FileSystem;
                if (fileSystem is "ext4" or "xfs")
                    return new(true, false, null, "This storage uses the built-in verified backup capture. No additional backup packages are needed.");
                if (fileSystem != "btrfs")
                    return new(false, false, null, "Automatic live backup capture supports local Btrfs, ext4 and XFS storage. No system packages were changed.");
            }
            var executable = await FindVerifiedToolAsync(ct);
            if (executable is not null)
            {
                if (previous is not null && !previous.Completed)
                    await store.SaveAsync(previous with { SchemaVersion = 2, ProcessId = null, StartedTicks = null, BootId = Guid.Empty, Completed = true }, ct);
                return new(true, false, executable, steamCmd ? "SteamCMD's 32-bit libraries are already installed." : compatibility ? "Python is already installed." : "The required snapshot tool is already installed. Capture permissions and storage support still need checking.");
            }
            if (!compatibility && !steamCmd && !notifications)
            {
                var mounts = LinuxMountTopology.Parse(await File.ReadAllTextAsync("/proc/self/mountinfo", ct));
                if (LinuxMountTopology.Resolve(Path.TrimEndingDirectorySeparator(Path.GetFullPath(managementRoot)), mounts).FileSystem != "btrfs")
                    return new(false, false, null, "Backup setup for this storage is not implemented yet. No system packages were changed.");
            }
            var distribution = await File.ReadAllTextAsync("/etc/os-release", ct);
            var plan = notifications ? LinuxNotificationToolPlan.Select(distribution) : steamCmd ? LinuxSteamCmdRuntimePlan.Select(distribution) : compatibility ? LinuxCompatibilityToolPlan.SelectPythonInstall(distribution) : LinuxBackupToolPlan.Select(distribution);
            if (!File.Exists(plan.Executable)) throw new FileNotFoundException("The distribution's supported package installer is missing.");
            var privileged = GetEffectiveUserId() != 0;
            if (privileged && !File.Exists("/usr/bin/pkexec")) throw new FileNotFoundException("Linux permission setup is unavailable; the package installation was not started.");
            var record = new LinuxPackageInstallRecord(2, Guid.NewGuid(), null, null, false);
            await store.SaveAsync(record, ct);
            // This unprivileged shell waits for a durable process checkpoint before launching the fixed package command.
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("read -r ready && [ \"$ready\" = go ] && exec \"$@\"");
            start.ArgumentList.Add("ark-required-tools");
            if (privileged) start.ArgumentList.Add("/usr/bin/pkexec");
            start.ArgumentList.Add(plan.Executable);
            foreach (var argument in plan.Arguments) start.ArgumentList.Add(argument);
            ct.ThrowIfCancellationRequested();
            using var installer = Process.Start(start) ?? throw new IOException("The package installer could not start.");
            try
            {
                var identity = await LinuxProcessIdentity.ReadAsync(installer.Id, ct)
                    ?? throw new IOException("The waiting installer exited before its identity could be recorded.");
                if (!identity.Alive) throw new IOException("The waiting installer exited before setup could begin.");
                record = record with { ProcessId = installer.Id, StartedTicks = identity.StartedTicks, BootId = identity.BootId };
                await store.SaveAsync(record, ct);
                await installer.StandardInput.WriteLineAsync("go".AsMemory(), ct);
                await installer.StandardInput.FlushAsync(ct);
                progress(new(notifications ? "Installing desktop notification support. Linux may ask for permission." : steamCmd ? "Installing SteamCMD's 32-bit libraries. Linux may ask for permission." : compatibility ? "Installing required Python tools. Linux may ask for permission." : "Installing the required Linux snapshot tool. Linux may ask for permission."));
                await installer.WaitForExitAsync(ct);
                if (installer.ExitCode != 0) throw new IOException("Linux tool installation failed or permission was declined. Check setup and retry.");
                executable = await FindVerifiedToolAsync(ct) ?? throw new IOException("The package installer completed, but the required tool is missing or unsupported.");
                await store.SaveAsync(record with { Completed = true }, ct);
                return new(true, true, executable, steamCmd ? "SteamCMD's 32-bit libraries installed." : compatibility ? "Python installed." : "Snapshot tool installed. Capture permissions and storage support still need checking.");
            }
            finally
            {
                // Never kill a system package transaction on cancellation or host shutdown.
                // Before 'go', EOF ends only the waiting shell. After 'go', the recorded package process can finish independently.
                installer.StandardInput.Close();
            }
        }
        finally { gate.Release(); }
    }

    private async Task<string?> FindVerifiedToolAsync(CancellationToken ct)
    {
        if (notifications) return LinuxNotificationToolPlan.LibrariesPresent() ? "libnotify.so.4" : null;
        if (steamCmd && !LinuxSteamCmdRuntimePlan.LibrariesPresent()) return null;
        foreach (var path in steamCmd ? new[] { "/lib/ld-linux.so.2", "/lib/i386-linux-gnu/ld-linux.so.2", "/lib32/ld-linux.so.2" }
            : compatibility ? new[] { "/usr/local/bin/python3", "/usr/bin/python3" } : new[] { "/usr/bin/btrfs", "/usr/sbin/btrfs" })
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
            if (steamCmd && !LinuxSteamCmdRuntimePlan.ValidFile(resolved)) continue;
            try { await VerifyAsync(resolved, ct); return resolved; }
            catch (IOException) { }
        }
        return null;
    }

    private async Task VerifyAsync(string executable, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new IOException("The installed snapshot tool could not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await Task.WhenAll(output, errors, process.WaitForExitAsync(deadline.Token));
            var text = (await output).Trim();
            if (text.Length == 0) text = (await errors).Trim();
            if (process.ExitCode != 0 || (steamCmd ? !LinuxSteamCmdRuntimePlan.SupportedLoaderVersion(text)
                : compatibility ? !LinuxCompatibilityToolPlan.SupportedPython(text) : !text.StartsWith("btrfs-progs", StringComparison.OrdinalIgnoreCase)))
                throw new IOException(steamCmd ? "SteamCMD's 32-bit loader could not be verified." : compatibility ? "ASA compatibility requires Python 3.10 or newer." : "The installed snapshot tool could not be verified.");
        }
        catch { if (!process.HasExited) process.Kill(); throw; }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}

