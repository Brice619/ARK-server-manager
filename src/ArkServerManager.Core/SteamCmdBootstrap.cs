using System.Formats.Tar;
using System.IO.Compression;
using System.Collections.Concurrent;

namespace ArkServerManager.Core;

/// <summary>Downloads the selected SteamCMD distribution into a new dedicated tool folder.</summary>
public sealed class SteamCmdBootstrap(HttpClient http)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public async Task<string> EnsureAsync(string directory, ServerPlatform platform, CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd(Path.GetFullPath(directory), _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await EnsureCoreAsync(directory, platform, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task<string> EnsureCoreAsync(string directory, ServerPlatform platform, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(platform)) throw new ArgumentOutOfRangeException(nameof(platform));
        var target = Path.GetFullPath(directory);
        RejectLinks(target);
        var filename = platform == ServerPlatform.Windows ? "steamcmd.exe" : "steamcmd.sh";
        var executable = Path.Combine(target, filename);
        RejectLinks(executable);
        var required = platform == ServerPlatform.Windows ? new[] { filename }
            : new[] { filename, "linux32/steamcmd", "linux32/libstdc++.so.6" };
        await RepairExecutablePermissionsAsync(target, platform, cancellationToken);
        if (Ready(target, required, platform)) return executable;
        if (File.Exists(target)) throw new IOException("The SteamCMD folder location is occupied by a file.");
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".steamcmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var uri = platform == ServerPlatform.Windows
                ? "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip"
                : "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var compressed = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (compressed.Length + read > 32 * 1024 * 1024) throw new InvalidDataException("SteamCMD download exceeds the expected size limit.");
                await compressed.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            compressed.Position = 0;
            long expanded = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (platform == ServerPlatform.Windows)
            {
                using var archive = new ZipArchive(compressed, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    var path = EntryPath(staging, entry.FullName, names);
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                    expanded += entry.Length;
                    if (expanded > 64 * 1024 * 1024) throw new InvalidDataException("SteamCMD archive is too large.");
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Linked archive entries are unsupported.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using var content = entry.Open();
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await content.CopyToAsync(output, cancellationToken);
                }
            }
            else
            {
                await RequireToolStoppedAsync(target, cancellationToken);
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                TarEntry? entry;
                while ((entry = await tar.GetNextEntryAsync(cancellationToken: cancellationToken)) is not null)
                {
                    var path = EntryPath(staging, entry.Name, names);
                    if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(path); continue; }
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        throw new InvalidDataException("Unsupported SteamCMD archive entry type.");
                    expanded += entry.Length;
                    if (expanded > 64 * 1024 * 1024) throw new InvalidDataException("SteamCMD archive is too large.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, cancellationToken);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, entry.Mode & (UnixFileMode)0x1FF);
                }
            }
            await RepairExecutablePermissionsAsync(staging, platform, cancellationToken);
            if (!Ready(staging, required, platform))
                throw new InvalidDataException("SteamCMD archive does not contain its required launcher, binary or libraries.");
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(target);
            if (!Directory.Exists(target)) Directory.Move(staging, target);
            else
            {
                // Preserve downloaded workshop items and self-update state; fill only missing bootstrap files.
                foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Path.Combine(target, Path.GetRelativePath(staging, file));
                    RejectLinks(destination);
                    if (Directory.Exists(destination)) throw new IOException("A SteamCMD bootstrap file is occupied by a folder.");
                    if (File.Exists(destination))
                    {
                        if (new FileInfo(destination).Length == 0)
                        {
                            await RequireToolStoppedAsync(target, cancellationToken);
                            RejectLinks(destination);
                            if (new FileInfo(destination).Length != 0)
                                throw new IOException("The SteamCMD file changed during repair. Retry preparation.");
                            File.Replace(file, destination, null, ignoreMetadataErrors: false);
                        }
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    RejectLinks(destination);
                    File.Move(file, destination, overwrite: false);
                }
            }
            RejectLinks(executable);
            if (!Ready(target, required, platform))
                throw new IOException("SteamCMD preparation did not produce its required executable and libraries.");
            return executable;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static bool Ready(string root, IReadOnlyList<string> required, ServerPlatform platform)
    {
        foreach (var relative in required)
        {
            var path = Path.Combine(root, relative); RejectLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
            if (OperatingSystem.IsLinux() && platform == ServerPlatform.Linux && relative is "steamcmd.sh" or "linux32/steamcmd"
                && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                throw new IOException("SteamCMD launcher or binary lacks executable permission.");
        }
        return true;
    }

    private static async Task RepairExecutablePermissionsAsync(string root, ServerPlatform platform, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || platform != ServerPlatform.Linux) return;
        var stoppedVerified = false;
        foreach (var relative in new[] { "steamcmd.sh", "linux32/steamcmd" })
        {
            var path = Path.Combine(root, relative);
            RejectLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) continue;
            if ((File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0) continue;
            if (!stoppedVerified)
            {
                await RequireToolStoppedAsync(root, ct);
                stoppedVerified = true;
            }
            ct.ThrowIfCancellationRequested();
            RejectLinks(path);
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        }
    }

    private static async Task RequireToolStoppedAsync(string root, CancellationToken ct)
    {
        var scan = await new SteamCmdProcessDiscovery().ScanAsync(ct);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (scan.UnresolvedProcesses != 0 || scan.Processes.Any(p => p.Executable.StartsWith(root + Path.DirectorySeparatorChar, comparison)))
            throw new IOException("SteamCMD is running or its process location could not be verified. Preparation will retry after it stops.");
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked SteamCMD locations require separate validation.");
    }

    private static string EntryPath(string root, string name, HashSet<string> names)
    {
        while (name.StartsWith("./")) name = name[2..];
        var trimmed = name.TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed) || trimmed.Contains('\\') || trimmed.Contains(':') || Path.IsPathRooted(trimmed)
            || trimmed.Split('/').Any(p => p is ".." or "." or "") || !names.Add(trimmed))
            throw new InvalidDataException("Invalid or repeated archive path.");
        return Path.Combine(root, trimmed);
    }
}
