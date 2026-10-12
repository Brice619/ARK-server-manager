namespace ArkServerManager.Core;

public sealed record LinuxPackageCommand(string Executable, IReadOnlyList<string> Arguments);

public static class LinuxBackupToolPlan
{
    public static LinuxPackageCommand Select(string osRelease)
    {
        if (osRelease.Length > 65536) throw new InvalidDataException("Linux distribution metadata is too large.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in osRelease.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator < 0) continue;
            var key = line[..separator].Trim();
            if (key is not ("ID" or "ID_LIKE")) continue;
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            if (!values.TryAdd(key, value)) throw new InvalidDataException("Repeated Linux distribution identity.");
        }
        var ids = (values.GetValueOrDefault("ID", "") + " " + values.GetValueOrDefault("ID_LIKE", ""))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length == 0 || ids.Any(id => id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
            throw new InvalidDataException("Invalid Linux distribution identity.");
        foreach (var id in ids)
        {
            if (id is "debian" or "ubuntu") return new("/usr/bin/apt-get", ["install", "--yes", "--no-install-recommends", "btrfs-progs"]);
            if (id is "fedora" or "rhel" or "centos") return new("/usr/bin/dnf", ["install", "--assumeyes", "btrfs-progs"]);
            if (id is "arch" or "manjaro") return new("/usr/bin/pacman", ["--sync", "--needed", "--noconfirm", "btrfs-progs"]);
        }
        throw new NotSupportedException("Automatic snapshot-tool installation is not implemented for this Linux distribution.");
    }
}
