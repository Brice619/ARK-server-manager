using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace ArkServerManager.Core;

public sealed record LinuxWineServerObservation(LinuxProcessIdentity Identity,
    IReadOnlyList<LinuxMappedArkPayload> Payloads, DateTimeOffset MeasuredAt);

/// <summary>Observes loaded ARK files between repeated kernel-identity checks. Does not confer ownership.</summary>
public static class LinuxWineServerObserver
{
    [SupportedOSPlatform("linux")]
    public static async Task<LinuxWineServerObservation?> ReadAsync(int processId, CancellationToken ct = default)
    {
        var before = await LinuxProcessIdentity.ReadAsync(processId, ct);
        if (before is null || !before.Alive) return null;
        var maps = "/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/maps";
        var first = LinuxMappedServerFiles.FindWindowsPayloads(await ReadMapsAsync(maps, ct));
        var second = LinuxMappedServerFiles.FindWindowsPayloads(await ReadMapsAsync(maps, ct));
        var after = await LinuxProcessIdentity.ReadAsync(processId, ct);
        if (after is null || !after.Alive) return null;
        ValidateStable(before, after, first, second);
        return new(after, second, DateTimeOffset.UtcNow);
    }

    internal static void ValidateStable(LinuxProcessIdentity before, LinuxProcessIdentity after,
        IReadOnlyList<LinuxMappedArkPayload> first, IReadOnlyList<LinuxMappedArkPayload> second)
    {
        if (!before.Alive || !after.Matches(before.ProcessId, before.BootId, before.StartedTicks)
            || !first.OrderBy(p => p.Executable, StringComparer.Ordinal).ThenBy(p => p.Inode)
                .SequenceEqual(second.OrderBy(p => p.Executable, StringComparer.Ordinal).ThenBy(p => p.Inode)))
            throw new IOException("The compatibility process or its loaded server file changed during observation.");
    }

    private static async Task<string> ReadMapsAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.Asynchronous);
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("The compatibility process memory map is too large to verify.");
            await bytes.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }
}
