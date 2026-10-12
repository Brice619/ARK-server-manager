using System.Globalization;
using System.Text.RegularExpressions;

namespace ArkServerManager.Core;

public sealed record LinuxMappedFile(ulong Start, ulong End, string Permissions, ulong Offset,
    string Device, ulong Inode, string Path, bool Deleted);
public sealed record LinuxMappedArkPayload(string Executable, string Directory, ArkEdition Edition,
    string Device, ulong Inode, bool Deleted);

/// <summary>Kernel file-mapping observations, not process ownership or server health.</summary>
public static partial class LinuxMappedServerFiles
{
    public static IReadOnlyList<LinuxMappedFile> Parse(string maps)
    {
        if (maps.Length > 8 * 1024 * 1024 || maps.Contains('\0')) throw new InvalidDataException("Invalid or oversized Linux memory map.");
        var files = new List<LinuxMappedFile>();
        ulong previousEnd = 0;
        foreach (var line in maps.Split('\n'))
        {
            if (line.Length == 0) continue;
            var match = MapLine().Match(line);
            if (!match.Success) throw new InvalidDataException("The Linux memory map has an incomplete entry.");
            ulong Hex(string key) => ulong.TryParse(match.Groups[key].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
                ? value : throw new InvalidDataException("Invalid Linux mapping number.");
            var start = Hex("start"); var end = Hex("end"); var offset = Hex("offset");
            _ = Hex("major"); _ = Hex("minor");
            if (end <= start || start < previousEnd || !ulong.TryParse(match.Groups["inode"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
                throw new InvalidDataException("Invalid Linux mapping range or file identity.");
            previousEnd = end;
            var path = match.Groups["path"].Value;
            if (!path.StartsWith('/')) continue;
            if (path.Contains('\\')) throw new InvalidDataException("Escaped Linux mapping paths require separate validation.");
            var deleted = path.EndsWith(" (deleted)", StringComparison.Ordinal);
            if (deleted) path = path[..^" (deleted)".Length];
            if (inode == 0) throw new InvalidDataException("A mapped file has no kernel file identity.");
            files.Add(new(start, end, match.Groups["permissions"].Value, offset,
                match.Groups["major"].Value + ":" + match.Groups["minor"].Value, inode, path, deleted));
        }
        return files;
    }

    public static IReadOnlyList<LinuxMappedArkPayload> FindWindowsPayloads(string maps)
    {
        var result = new List<LinuxMappedArkPayload>();
        foreach (var group in Parse(maps).GroupBy(file => (file.Path, file.Device, file.Inode, file.Deleted)))
        {
            if (!group.Any(file => file.Permissions[2] == 'x')) continue;
            foreach (var layout in InstallationDiscovery.Executables.Where(layout => layout.Platform == "Windows"))
            {
                var suffix = "/" + layout.Path;
                if (!group.Key.Path.EndsWith(suffix, StringComparison.Ordinal)) continue;
                var directory = group.Key.Path[..^suffix.Length];
                if (directory.Length == 0 || directory.Split('/').Any(part => part is "." or ".."))
                    throw new InvalidDataException("The mapped server path is ambiguous.");
                result.Add(new(group.Key.Path, directory, layout.Edition, group.Key.Device, group.Key.Inode, group.Key.Deleted));
            }
        }
        return result;
    }

    [GeneratedRegex(@"^(?<start>[0-9a-fA-F]+)-(?<end>[0-9a-fA-F]+) +(?<permissions>[r-][w-][x-][ps]) +(?<offset>[0-9a-fA-F]+) +(?<major>[0-9a-fA-F]+):(?<minor>[0-9a-fA-F]+) +(?<inode>[0-9]+)(?: +(?<path>.*))?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex MapLine();
}
