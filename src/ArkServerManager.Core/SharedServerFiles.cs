using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace ArkServerManager.Core;

public sealed record SharedGameFile(string Path, long Bytes, string SHA256, bool Shared);
public sealed record SharedGamePackage(int SchemaVersion, ArkEdition Edition, ServerPlatform Platform, IReadOnlyList<SharedGameFile> Files);
public sealed record SharedServerFileRecord(int SchemaVersion, string ManagementRoot, ArkEdition Edition, ServerPlatform Platform, IReadOnlyList<string> SharedPaths);

/// <summary>One physical copy of shipped game files; writable server state is never linked.</summary>
public sealed class SharedServerFiles(string managementRoot, ArkEdition edition, ServerPlatform platform)
{
    public const string Marker = ".ark-shared-files.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root = Path.GetFullPath(managementRoot);
    public string PoolDirectory => Path.Combine(root, "GameFiles", edition.ToString(), SteamCmdInstaller.PayloadPlatform(edition, platform).ToString());
    private string Manifest => Path.Combine(PoolDirectory, "package.json");
    private string Blob(string hash) => Path.Combine(PoolDirectory, "files", hash);

    public async Task<bool> ReuseAsync(string destination, CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd(PoolDirectory, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var package = await ReadPackageAsync(ct);
            if (package is null) return false;
            await MaterializeAsync(package, destination, ct);
            CollectUnused(package);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task RegisterAsync(string source, CancellationToken ct = default)
    {
        RequireServerDirectory(source);
        var gate = Gates.GetOrAdd(PoolDirectory, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            RequirePackage(source);
            RejectLinks(PoolDirectory);
            Directory.CreateDirectory(Path.Combine(PoolDirectory, "files"));
            var entries = new List<SharedGameFile>();
            foreach (var file in PackageFiles(source))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bytes = input.Length;
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
                var shared = CanShare(relative);
                var blob = Blob(hash);
                if (!File.Exists(blob))
                {
                    if (shared) NativeGameFileLinks.Create(blob, file);
                    else File.Copy(file, blob);
                }
                else if (new FileInfo(blob).Length != bytes) throw new IOException("Shared game-file storage is damaged.");
                entries.Add(new(relative, bytes, hash, shared));
            }
            var package = new SharedGamePackage(1, edition, platform, entries);
            // Publish only a complete package, then link existing files to its verified content.
            await new AtomicJsonStore<SharedGamePackage>(Manifest).SaveAsync(package, ct);
            await MaterializeAsync(package, source, ct);
            CollectUnused(package);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> AdoptExistingAsync(CatalogRepository repository, string destination, CancellationToken ct)
    {
        if (await ReuseAsync(destination, ct)) return true;
        var recorded = await ServerInstallationPreparation.ReadInventoryAsync(root, ct);
        var catalog = await repository.ReadAsync(ct);
        var sources = recorded.Records.Where(r => r.Edition == edition && r.Platform == platform && r.Phase == ServerInstallationPhase.FilesReady)
            .SelectMany(r => new[] { r.StagingDirectory, r.FinalDirectory })
            .Concat(catalog.Servers.Where(s => s.Edition == edition).Select(s => s.Directory)).Distinct();
        foreach (var source in sources)
        {
            if (Path.GetFullPath(source) == Path.GetFullPath(destination) || !Directory.Exists(source)
                || !IsManagedServerDirectory(source)) continue;
            var candidate = new InstallationDiscovery().Evaluate(source).Candidates.Any(c => c.Edition == edition
                && c.Platform == SteamCmdInstaller.PayloadPlatform(edition, platform).ToString());
            if (!candidate || (await new InstallationStopGuard().CheckAsync(source, ct)).State != InstallationStopState.Stopped) continue;
            await RegisterAsync(source, ct);
            return await ReuseAsync(destination, ct);
        }
        return false;
    }

    public static async Task DetachBeforeWriteAsync(string directory, CancellationToken ct)
    {
        var record = await ReadMarkerAsync(directory, ct);
        if (record is null) return;
        var store = new SharedServerFiles(record.ManagementRoot, record.Edition, record.Platform);
        store.RequireServerDirectory(directory);
        foreach (var relative in record.SharedPaths)
        {
            var path = ResolveRelative(directory, relative);
            if (!File.Exists(path) || NativeGameFileLinks.Count(path) <= 1) continue;
            ct.ThrowIfCancellationRequested();
            RejectLinks(path);
            var temporary = path + ".ark-private-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(path, temporary);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public static async Task RefreshAfterWriteAsync(SteamInstallRequest request, CancellationToken ct)
    {
        var record = await ReadMarkerAsync(request.Directory, ct);
        if (record is null) return;
        if (record.Edition != request.Edition || record.Platform != request.Platform) throw new IOException("Shared package identity changed.");
        await new SharedServerFiles(record.ManagementRoot, record.Edition, record.Platform).RegisterAsync(request.Directory, ct);
    }

    public static async Task RefreshBeforeStartAsync(string managementRoot, ServerDefinition server, ServerPlatform platform, CancellationToken ct)
    {
        var record = await ReadMarkerAsync(server.Directory, ct);
        if (record is null) return; // Existing external installations retain their original ownership.
        if (record.ManagementRoot != Path.GetFullPath(managementRoot) || record.Edition != server.Edition || record.Platform != platform)
            throw new IOException("Shared package identity changed.");
        await new SharedServerFiles(managementRoot, server.Edition, platform).ReuseAsync(server.Directory, ct);
    }

    private async Task<SharedGamePackage?> ReadPackageAsync(CancellationToken ct)
    {
        RejectLinks(Manifest);
        var package = await new AtomicJsonStore<SharedGamePackage>(Manifest).LoadAsync(ct);
        if (package is null) return null;
        if (package.SchemaVersion != 1 || package.Edition != edition || package.Platform != platform || package.Files.Count == 0)
            throw new IOException("Shared package identity is invalid.");
        foreach (var file in package.Files)
        {
            ResolveRelative(root, file.Path);
            if (file.SHA256.Length != 64 || !file.SHA256.All(char.IsAsciiHexDigit) || file.Shared != CanShare(file.Path))
                throw new IOException("Shared file record is invalid.");
            RejectLinks(Blob(file.SHA256));
            if (!File.Exists(Blob(file.SHA256)) || new FileInfo(Blob(file.SHA256)).Length != file.Bytes)
                throw new IOException("Shared game files are missing. Repair the installed game package.");
        }
        return package;
    }

    private async Task MaterializeAsync(SharedGamePackage package, string destination, CancellationToken ct)
    {
        RequireServerDirectory(destination);
        Directory.CreateDirectory(destination);
        var previous = await ReadMarkerAsync(destination, ct);
        // Record ownership before creating links so an interrupted setup/update can safely detach them.
        await new AtomicJsonStore<SharedServerFileRecord>(Path.Combine(destination, Marker)).SaveAsync(
            new(1, root, edition, platform, package.Files.Where(f => f.Shared).Select(f => f.Path).ToArray()), ct);
        foreach (var file in package.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = ResolveRelative(destination, file.Path);
            RejectLinks(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (file.Shared && File.Exists(path) && NativeGameFileLinks.SameFile(path, Blob(file.SHA256))) continue;
            var temporary = path + ".ark-shared-" + Guid.NewGuid().ToString("N");
            try
            {
                if (file.Shared) NativeGameFileLinks.Create(temporary, Blob(file.SHA256));
                else File.Copy(Blob(file.SHA256), temporary);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        RequirePackage(destination);
        var currentPaths = package.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (previous is not null)
            foreach (var oldPath in previous.SharedPaths.Where(p => !currentPaths.Contains(p)))
            {
                var obsolete = ResolveRelative(destination, oldPath);
                RejectLinks(obsolete);
                if (File.Exists(obsolete)) File.Delete(obsolete);
            }
    }

    private void CollectUnused(SharedGamePackage package)
    {
        var latest = package.Files.Select(f => f.SHA256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(PoolDirectory, "files")))
            if (!latest.Contains(Path.GetFileName(path)) && NativeGameFileLinks.Count(path) == 1) File.Delete(path);
    }

    private void RequirePackage(string directory)
    {
        if (!new InstallationDiscovery().Evaluate(directory).Candidates.Any(c => c.Edition == edition
            && c.Platform == SteamCmdInstaller.PayloadPlatform(edition, platform).ToString() && new FileInfo(c.Executable).Length > 0))
            throw new IOException("The shared game package does not contain the expected server executable.");
    }
    private bool IsManagedServerDirectory(string directory)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
        return string.Equals(parent, Path.Combine(root, "servers"), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    private void RequireServerDirectory(string directory)
    {
        if (!IsManagedServerDirectory(directory)) throw new IOException("Shared game files require a server folder inside this installation.");
        RejectLinks(directory);
    }
    private static IEnumerable<string> PackageFiles(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinks(entry);
            var relative = Path.GetRelativePath(directory, entry).Replace('\\', '/');
            if (Excluded(relative)) continue;
            if (Directory.Exists(entry))
                foreach (var file in Walk(entry, relative)) yield return file;
            else yield return entry;
        }
        static IEnumerable<string> Walk(string folder, string relativeFolder)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                RejectLinks(entry);
                var relative = relativeFolder + "/" + Path.GetFileName(entry);
                if (Excluded(relative)) continue;
                if (Directory.Exists(entry)) { foreach (var child in Walk(entry, relative)) yield return child; }
                else yield return entry;
            }
        }
    }
    private static bool Excluded(string relative)
    {
        var segments = relative.Split('/');
        if (segments.Any(s => new[] { "Saved", "logs", "backups", "downloads", "downloading", "workshop", "temp", "cache" }.Contains(s, StringComparer.OrdinalIgnoreCase))) return true;
        if (segments.Any(s => s.StartsWith(".ark-", StringComparison.OrdinalIgnoreCase) || s.Contains(".ark-private-", StringComparison.OrdinalIgnoreCase)
            || s.Contains(".ark-shared-", StringComparison.OrdinalIgnoreCase))) return true;
        var mods = Array.FindIndex(segments, s => s.Equals("Mods", StringComparison.OrdinalIgnoreCase));
        if (mods >= 0)
        {
            if (!relative.StartsWith("ShooterGame/Content/Mods", StringComparison.OrdinalIgnoreCase)) return true;
            if (mods + 1 < segments.Length && char.IsAsciiDigit(segments[mods + 1][0])) return true;
        }
        return false;
    }
    private static bool CanShare(string relative) => !Excluded(relative)
        && !relative.Split('/').Any(s => s.Equals("Config", StringComparison.OrdinalIgnoreCase))
        && (relative.StartsWith("ShooterGame/Content/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("Engine/Content/", StringComparison.OrdinalIgnoreCase)
            || new[] { ".exe", ".dll", ".so", ".pdb", ".uasset", ".umap", ".uexp", ".ubulk", ".pak", ".ucas", ".utoc", ".bin", ".mp4", ".wem", ".bnk" }
                .Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase)
            || relative.Equals("ShooterGame/Binaries/Linux/ShooterGameServer", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("ShooterGame/Binaries/Linux/ShooterGame", StringComparison.OrdinalIgnoreCase));
    private static string ResolveRelative(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(s => s is ".." or "." or ""))
            throw new IOException("Invalid shared package path.");
        return Path.Combine(Path.GetFullPath(directory), relative.Replace('/', Path.DirectorySeparatorChar));
    }
    private static async Task<SharedServerFileRecord?> ReadMarkerAsync(string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, Marker); RejectLinks(path);
        var record = await new AtomicJsonStore<SharedServerFileRecord>(path).LoadAsync(ct);
        if (record is not null && (record.SchemaVersion != 1 || !Enum.IsDefined(record.Edition) || !Enum.IsDefined(record.Platform)
            || !Path.IsPathFullyQualified(record.ManagementRoot) || record.SharedPaths.Any(p => !CanShare(p))))
            throw new IOException("Invalid shared server-file ownership record.");
        return record;
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Shared game-file paths cannot use symbolic links or junctions.");
    }
}

public static class NativeGameFileLinks
{
    public static bool SameFile(string first, string second)
    {
        if (OperatingSystem.IsWindows())
        {
            using var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(a.SafeFileHandle, out var one) || !GetFileInformationByHandle(b.SafeFileHandle, out var two))
                throw new IOException("Could not inspect shared game-file identity.");
            return one.VolumeSerial == two.VolumeSerial && one.IndexHigh == two.IndexHigh && one.IndexLow == two.IndexLow;
        }
        var firstInfo = new byte[256]; var secondInfo = new byte[256];
        if (!OperatingSystem.IsLinux() || Statx(-100, first, 0x100, 0x100, firstInfo) != 0 || Statx(-100, second, 0x100, 0x100, secondInfo) != 0)
            throw new IOException("Could not inspect shared game-file identity.");
        return firstInfo.AsSpan(32, 8).SequenceEqual(secondInfo.AsSpan(32, 8))
            && firstInfo.AsSpan(136, 8).SequenceEqual(secondInfo.AsSpan(136, 8));
    }
    public static void Create(string destination, string source)
    {
        var success = OperatingSystem.IsWindows() ? CreateHardLink(destination, source, IntPtr.Zero)
            : OperatingSystem.IsLinux() ? Link(source, destination) == 0 : throw new PlatformNotSupportedException();
        if (!success) throw new IOException("Game-file sharing requires a filesystem with hard-link support on the same drive.",
            new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
    }
    public static uint Count(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information)) throw new IOException("Could not inspect shared game-file ownership.");
            return information.NumberOfLinks;
        }
        var buffer = new byte[256];
        if (!OperatingSystem.IsLinux() || Statx(-100, path, 0x100, 0x4, buffer) != 0) throw new IOException("Could not inspect shared game-file ownership.");
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string destination, string source, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);
}
