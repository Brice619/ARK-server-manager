using System.Buffers.Binary;
using System.Globalization;

namespace ArkServerManager.Core;

/// <summary>Checks local installed files after native startup; does not prove mod gameplay behavior.</summary>
public static class AseInstalledModFiles
{
    public static Task RequireAsync(ServerDefinition server, CancellationToken ct = default)
    {
        if (server.Edition != ArkEdition.ASE || server.Mods.Count == 0) return Task.CompletedTask;
        if (!Path.IsPathFullyQualified(server.Directory)) throw new ArgumentException("Choose an absolute server folder.");
        AsaModLaunchArguments.Build(server.Mods);
        var root = Path.Combine(server.Directory, "ShooterGame", "Content", "Mods");
        foreach (var selectedId in server.Mods)
        {
            var numericId = ulong.Parse(selectedId, CultureInfo.InvariantCulture);
            var id = numericId.ToString(CultureInfo.InvariantCulture);
            ct.ThrowIfCancellationRequested();
            var descriptor = Path.Combine(root, id + ".mod"); var folder = Path.Combine(root, id);
            RejectLinks(descriptor); RejectLinks(folder);
            if (!File.Exists(descriptor) || new FileInfo(descriptor).Length < 8 || !Directory.Exists(folder)
                || !DescriptorMatches(descriptor, numericId) || !HasUnpackedAsset(folder, ct))
                throw new IOException($"ASE mod {id} did not finish installing. Its descriptor or unpacked assets are missing, incomplete or do not match the selected ID. Review the mod installation before starting again.");
        }
        return Task.CompletedTask;
    }
    private static bool DescriptorMatches(string path, ulong expectedId)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 8) return false;
        Span<byte> header = stackalloc byte[8]; input.ReadExactly(header);
        return BinaryPrimitives.ReadUInt64LittleEndian(header) == expectedId;
    }
    private static bool HasUnpackedAsset(string folder, CancellationToken ct)
    {
        var entries = 0;
        var found = false;
        Visit(folder, 0);
        return found;
        void Visit(string directory, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 128) throw new IOException("The installed mod tree is too deeply nested. Review its files.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > 1_000_000) throw new IOException("The installed mod contains too many entries for readiness verification. Review its files.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked installed mod content requires review.");
                if ((attributes & FileAttributes.Directory) != 0) Visit(path, depth + 1);
                else
                {
                    var extension = Path.GetExtension(path);
                    if ((extension.Equals(".uasset", StringComparison.OrdinalIgnoreCase) || extension.Equals(".umap", StringComparison.OrdinalIgnoreCase))
                        && new FileInfo(path).Length > 0) found = true;
                }
            }
        }
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked installed mod locations require review.");
    }
}
