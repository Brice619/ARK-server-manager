using System.Security.Cryptography;

namespace ArkServerManager.Core;

public sealed record InstallationTransferProof(string Source, string Destination, string RetiredDirectory,
    Dictionary<string, string> Files, List<string> Directories);

/// <summary>Content evidence recorded before publishing a transferred installation.</summary>
public static class InstallationTransferProofs
{
    public static async Task<InstallationTransferProof> CaptureAsync(string directory, string source, string destination,
        string retired, CancellationToken ct = default)
    {
        CheckAncestors(directory);
        var files = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var directories = new List<string>();
        foreach (var entry in Enumerate(directory))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(directory, entry);
            if (Directory.Exists(entry)) directories.Add(relative);
            else files.Add(relative, await HashAsync(entry, ct));
        }
        return new(Path.GetFullPath(source), Path.GetFullPath(destination), Path.GetFullPath(retired), files, directories);
    }

    public static async Task VerifyAsync(InstallationTransferProof proof, string directory, CancellationToken ct = default)
    {
        Validate(proof);
        CheckAncestors(directory);
        if (!Directory.Exists(directory)) throw new IOException("The recorded transferred folder is missing.");
        var expected = proof.Files.Keys.Concat(proof.Directories).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var actual = Enumerate(directory).Select(path => Path.GetRelativePath(directory, path)).ToHashSet(expected.Comparer);
        if (!actual.SetEquals(expected)) throw new IOException("The transferred installation has changed since publication. Inspect it before recovery.");
        foreach (var relative in proof.Directories)
            if (!Directory.Exists(Path.Combine(directory, relative))) throw new IOException("A transferred directory changed type.");
        foreach (var (relative, hash) in proof.Files)
            if (!File.Exists(Path.Combine(directory, relative)) || !StringComparer.OrdinalIgnoreCase.Equals(await HashAsync(Path.Combine(directory, relative), ct), hash))
                throw new IOException("A transferred file changed since publication. Inspect it before recovery.");
    }

    public static void Validate(InstallationTransferProof proof)
    {
        if (proof.Files is null || proof.Directories is null || proof.Files.Count + proof.Directories.Count > 1_000_000
            || new[] { proof.Source, proof.Destination, proof.RetiredDirectory }.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException("Invalid import file evidence.");
        foreach (var relative in proof.Files.Keys.Concat(proof.Directories))
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part is ".." or "." or ""))
                throw new InvalidDataException("Invalid path in import file evidence.");
        }
        if (proof.Files.Values.Any(hash => hash is null || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))))
            throw new InvalidDataException("Invalid import checksum.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(proof.Source));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(proof.Destination));
        if (source.Equals(target, comparison) || source.StartsWith(target + Path.DirectorySeparatorChar, comparison)
            || target.StartsWith(source + Path.DirectorySeparatorChar, comparison)
            || Path.GetDirectoryName(proof.RetiredDirectory) != Path.GetDirectoryName(source)
            || !Path.GetFileName(proof.RetiredDirectory).StartsWith(".import-retired-", StringComparison.Ordinal)
            || !Guid.TryParseExact(Path.GetFileName(proof.RetiredDirectory)[".import-retired-".Length..], "N", out _))
            throw new InvalidDataException("Invalid import source and destination relationship.");
    }

    internal static void CheckAncestors(string path)
    {
        for (var entry = new DirectoryInfo(Path.GetFullPath(path)); entry is not null; entry = entry.Parent)
            if ((entry.Exists || File.Exists(entry.FullName)) && (File.GetAttributes(entry.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Import recovery refuses linked paths.");
    }
    private static IEnumerable<string> Enumerate(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Import evidence refuses linked files.");
            yield return entry;
            if (Directory.Exists(entry)) foreach (var child in Enumerate(entry)) yield return child;
        }
    }
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
}
