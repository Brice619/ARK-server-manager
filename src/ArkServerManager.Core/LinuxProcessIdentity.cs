using System.Globalization;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;

namespace ArkServerManager.Core;

/// <summary>Kernel process identity, independent of wall-clock adjustments and PID reuse.</summary>
public sealed record LinuxProcessIdentity(int ProcessId, Guid BootId, long StartedTicks, bool Alive)
{
    public bool Matches(int processId, Guid bootId, long startedTicks)
        => Alive && ProcessId == processId && BootId == bootId && StartedTicks == startedTicks;

    public static LinuxProcessIdentity Parse(int expectedId, Guid bootId, string stat)
    {
        var opening = stat.IndexOf('('); var closing = stat.LastIndexOf(')');
        if (stat.Length > 65536 || expectedId <= 0 || bootId == Guid.Empty || opening < 1 || closing <= opening
            || !int.TryParse(stat[..opening].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id != expectedId)
            throw new InvalidDataException("Invalid Linux process identity.");
        var fields = stat[(closing + 1)..].Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        // The suffix begins at field 3 (state); field 22 is the kernel start tick.
        if (fields.Length < 20 || fields[0].Length != 1 || !"RSDZTtXxKWPI".Contains(fields[0][0])
            || !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var started))
            throw new InvalidDataException("Incomplete Linux process identity.");
        return new(id, bootId, started, fields[0] is not ("Z" or "X" or "x"));
    }

    [SupportedOSPlatform("linux")]
    public static async Task<LinuxProcessIdentity?> ReadAsync(int processId, CancellationToken ct = default)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        var bootText = await File.ReadAllTextAsync("/proc/sys/kernel/random/boot_id", ct);
        if (!Guid.TryParse(bootText.Trim(), out var boot) || boot == Guid.Empty)
            throw new InvalidDataException("The Linux boot identity could not be verified.");
        string stat;
        try { stat = await File.ReadAllTextAsync("/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/stat", ct); }
        catch (FileNotFoundException) { return RequireMissingProcess(processId); }
        catch (DirectoryNotFoundException) { return RequireMissingProcess(processId); }
        return Parse(processId, boot, stat);
    }

    private static LinuxProcessIdentity? RequireMissingProcess(int id)
    {
        // Signal zero only checks existence/permission; it never stops or terminates a process.
        if (ProbeProcess(id, 0) != 0 && Marshal.GetLastPInvokeError() == 3) return null; // ESRCH
        throw new IOException("The process may still exist, but its Linux identity is unavailable. Preserve its recorded operation.");
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int ProbeProcess(int processId, int signal);
}
