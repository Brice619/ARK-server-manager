using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace ArkServerManager.Core;

internal static class SteamCmdProcessGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public static SemaphoreSlim For(string executable) => Gates.GetOrAdd(Path.GetFullPath(executable), _ => new(1, 1));
}

public sealed class SteamBuildQuery(string executable, string logDirectory)
{
    public async Task<SteamBuild> QueryAsync(ArkEdition edition, TimeSpan timeout, CancellationToken ct = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var exe = Path.GetFullPath(executable);
        var gate = SteamCmdProcessGate.For(exe);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        await gate.WaitAsync(deadline.Token);
        try
        {
            var id = SteamCmdInstaller.AppId(edition);
            for (var attempt = 0; attempt < 2; attempt++)
            {
            var log = Path.Combine(Path.GetFullPath(logDirectory), $"steam-metadata-{id}-{Guid.NewGuid():N}.log");
            await using var process = ManagedProcess.Start(new(exe, Path.GetDirectoryName(exe)!,
                ["+@ShutdownOnFailedCommand", "1", "+@NoPromptForPassword", "1", "+login", "anonymous", "+app_info_update", "1",
                 "+app_info_print", id.ToString(CultureInfo.InvariantCulture), "+quit"], log));
            var result = await process.Completion.WaitAsync(deadline.Token);
            if (result.ExitCode != 0)
            {
                if (attempt == 1) throw new IOException("SteamCMD metadata query failed. See its log.");
                // First launch may self-update and hand off to a replacement process. Never overlap it.
                var root = Path.GetDirectoryName(exe)! + Path.DirectorySeparatorChar;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                while (true)
                {
                    var scan = await new SteamCmdProcessDiscovery().ScanAsync(deadline.Token);
                    if (scan.UnresolvedProcesses != 0) throw new IOException("SteamCMD process ownership could not be verified after its first launch.");
                    if (!scan.Processes.Any(p => p.Executable.StartsWith(root, comparison))) break;
                    await Task.Delay(500, deadline.Token);
                }
                continue;
            }
            if (process.LogFailure is not null) throw new IOException("SteamCMD metadata could not be recorded.", process.LogFailure);
            var text = new StringBuilder();
            using var reader = File.OpenText(log);
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                var marker = line.IndexOf(" stdout ", StringComparison.Ordinal);
                if (marker < 0) continue;
                text.AppendLine(line[(marker + 8)..]);
                if (text.Length > 16 * 1024 * 1024) throw new InvalidDataException("SteamCMD metadata is too large.");
            }
            return SteamBuildMetadata.ReadAvailable(text.ToString(), id);
            }
            throw new IOException("SteamCMD metadata query did not complete.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("SteamCMD metadata query timed out."); }
        finally { gate.Release(); }
    }
}
