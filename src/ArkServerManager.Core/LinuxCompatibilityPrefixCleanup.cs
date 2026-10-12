using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace ArkServerManager.Core;

public static class LinuxCompatibilityPrefixCleanup
{
    public static void ValidateRecord(LinuxAsaPrefixRecord record, Guid serverId, string expectedPrefix)
    {
        if (record.SchemaVersion != 1 || record.ServerId != serverId || record.Prefix != expectedPrefix || record.OperationId == Guid.Empty
            || record.ProcessId.HasValue != record.StartedTicks.HasValue || record.ProcessId is <= 0 || record.StartedTicks is < 0
            || record.ProcessId.HasValue && record.BootId == Guid.Empty)
            throw new InvalidDataException("Compatibility prefix ownership requires review.");
    }

    [SupportedOSPlatform("linux")]
    public static async Task RemoveAsync(string managementRoot, Guid serverId, CancellationToken ct = default)
    {
        var prefix = UmuServerEnvironment.Build(managementRoot, serverId)["WINEPREFIX"]!;
        var recordPath = Path.Combine(managementRoot, "app-settings", "asa-prefix-" + serverId.ToString("N") + ".json");
        RejectAncestors(prefix); RejectAncestors(recordPath);
        var record = await CoordinatedBackupRecords.ReadBoundedAsync<LinuxAsaPrefixRecord>(recordPath, ct);
        if (record is null)
        {
            if (Path.Exists(prefix)) throw new IOException("Compatibility prefix exists without an ownership record. Preserve it for review.");
            return;
        }
        ValidateRecord(record, serverId, prefix);
        if (record.ProcessId is int owner && (await LinuxProcessIdentity.ReadAsync(owner, ct))?.Matches(owner, record.BootId, record.StartedTicks!.Value) == true)
            throw new IOException("The recorded compatibility process is still running. Prefix cleanup will retry after it exits.");
        await RequireUnusedAsync(prefix, ct);
        var log = Path.Combine(managementRoot, "Dependencies", "Compatibility", "Logs", serverId.ToString("N") + ".log");
        RejectAncestors(log);
        if (Directory.Exists(prefix)) RemoveTree(prefix, ct, 0);
        else if (File.Exists(prefix)) throw new IOException("A file replaced the compatibility prefix.");
        if (File.Exists(log)) File.Delete(log);
        File.Delete(recordPath);
    }

    [SupportedOSPlatform("linux")]
    private static async Task RequireUnusedAsync(string prefix, CancellationToken ct)
    {
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var name = process.ProcessName;
                    if (!(name.Contains("wine", StringComparison.OrdinalIgnoreCase) || name.Contains("proton", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("python", StringComparison.OrdinalIgnoreCase) || name.Contains("umu", StringComparison.OrdinalIgnoreCase))) continue;
                    var before = await LinuxProcessIdentity.ReadAsync(process.Id, ct);
                    if (before?.Alive != true) continue;
                    await using var input = new FileStream("/proc/" + process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/environ", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var buffer = new byte[1024 * 1024 + 1]; var total = 0;
                    while (total < buffer.Length)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(total), ct);
                        if (read == 0) break;
                        total += read;
                    }
                    if (total == buffer.Length) throw new IOException("Compatibility process environment exceeds its inspection limit.");
                    var after = await LinuxProcessIdentity.ReadAsync(process.Id, ct);
                    if (after?.Alive != true) continue;
                    if (!after.Matches(before.ProcessId, before.BootId, before.StartedTicks)) throw new IOException("Compatibility process identity changed during cleanup review.");
                    var environment = new UTF8Encoding(false, true).GetString(buffer, 0, total);
                    if (environment.Split('\0').Any(item => item.StartsWith("WINEPREFIX=", StringComparison.Ordinal)
                        && Path.GetFullPath(item[11..]) == prefix)) throw new IOException("A compatibility process still uses this server's prefix.");
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (FileNotFoundException)
                { if (await LinuxProcessIdentity.ReadAsync(process.Id, ct) is not null) throw; }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static void RemoveTree(string directory, CancellationToken ct, int depth)
    {
        ct.ThrowIfCancellationRequested();
        RejectAncestors(directory);
        if (depth > 128) throw new IOException("Compatibility prefix nesting requires review.");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            ct.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            // Wine deliberately creates drive links. Unlink them without traversing their targets.
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            { if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path); else File.Delete(path); }
            else if ((attributes & FileAttributes.Directory) != 0) RemoveTree(path, ct, depth + 1);
            else File.Delete(path);
        }
        Directory.Delete(directory);
    }
    private static void RejectAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked compatibility ownership paths require review.");
    }
}
