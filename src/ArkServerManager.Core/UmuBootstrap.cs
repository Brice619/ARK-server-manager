using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Security.Cryptography;

namespace ArkServerManager.Core;

/// <summary>Installs the pinned upstream zipapp; Python/system/runtime checks are separate prerequisites.</summary>
public sealed class UmuBootstrap(HttpClient http)
{
    public const string Version = "1.4.4";
    private const string ArchiveHash = "eb590691841f7fad3fc3ad8fd5db4ccb87849fe7948e62b28ece7a4ee48cc851";
    private const string LauncherHash = "d0005a58602041229cc467dab03dc0c0b9e8cce09a8145b16b7683244cf17804";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public async Task<string> EnsureAsync(string directory, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Choose an absolute tool folder.");
        var root = Path.GetFullPath(directory);
        var gate = Gates.GetOrAdd(root, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var executable = Path.Combine(root, "umu-run");
            RejectLinks(executable);
            if (File.Exists(executable))
            {
                await using var existing = File.OpenRead(executable);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(existing, ct)).Equals(LauncherHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The existing compatibility launcher differs from the managed version. Review it before replacing it.");
                RepairMode(executable);
                return executable;
            }
            using var response = await http.GetAsync($"https://github.com/Open-Wine-Components/umu-launcher/releases/download/{Version}/umu-launcher-{Version}-zipapp.tar",
                HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var download = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (download.Length + read > 1024 * 1024) throw new InvalidDataException("The compatibility launcher download exceeds its size limit.");
                await download.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            var launcher = VerifyAndReadLauncher(download.ToArray(), ArchiveHash, LauncherHash);
            RejectLinks(executable);
            Directory.CreateDirectory(root);
            var temporary = Path.Combine(root, ".umu-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
                { await output.WriteAsync(launcher, ct); await output.FlushAsync(ct); output.Flush(true); }
                RepairMode(temporary);
                ct.ThrowIfCancellationRequested();
                RejectLinks(executable);
                File.Move(temporary, executable, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return executable;
        }
        finally { gate.Release(); }
    }

    internal static byte[] VerifyAndReadLauncher(byte[] archive, string archiveHash, string launcherHash)
    {
        if (!Convert.ToHexString(SHA256.HashData(archive)).Equals(archiveHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The compatibility launcher archive failed verification.");
        using var bytes = new MemoryStream(archive);
        using var reader = new TarReader(bytes);
        byte[]? launcher = null;
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.Name == "umu/" && entry.EntryType == TarEntryType.Directory) continue;
            // Upstream's Python alias is unnecessary when invoking the zipapp directly.
            if (entry.Name == "umu/umu_run.py" && entry.EntryType == TarEntryType.SymbolicLink && entry.LinkName == "umu-run") continue;
            if (entry.Name != "umu/umu-run" || entry.EntryType != TarEntryType.RegularFile || launcher is not null || entry.Length is <= 0 or > 1024 * 1024)
                throw new InvalidDataException("The compatibility archive has an unexpected entry.");
            using var content = new MemoryStream();
            entry.DataStream!.CopyTo(content);
            launcher = content.ToArray();
        }
        if (launcher is null || !Convert.ToHexString(SHA256.HashData(launcher)).Equals(launcherHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The compatibility launcher failed verification.");
        return launcher;
    }

    private static void RepairMode(string path)
    {
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked compatibility-tool paths require review.");
    }
}
