using System.Diagnostics;
using System.Reflection.PortableExecutable;
namespace ArkServerManager.Core;
public sealed record ServerRuntimeLibrary(string Name, string Path, bool Ready, string Message);
public static class WindowsServerRuntimeLibraries
{
    public static IReadOnlyList<ServerRuntimeLibrary> Check(ServerDefinition server)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows runtime checks require Windows.");
        var binary = server.Edition switch
        {
            ArkEdition.ASE => "ShooterGameServer.exe", ArkEdition.ASA => "ArkAscendedServer.exe",
            _ => throw new ArgumentException("Choose an ARK edition.")
        };
        var directory = Path.Combine(server.Directory, "ShooterGame", "Binaries", "Win64");
        using var source = File.OpenRead(Path.Combine(directory, binary)); using var executable = new PEReader(source);
        var header = executable.PEHeaders.PEHeader ?? throw new BadImageFormatException("Missing native executable header.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (header.ImportTableDirectory.RelativeVirtualAddress != 0)
        {
            var imports = executable.GetSectionData(header.ImportTableDirectory.RelativeVirtualAddress).GetReader();
            for (var count = 0; count < 1024 && imports.RemainingBytes >= 20; count++)
            {
                imports.ReadUInt32(); imports.ReadUInt32(); imports.ReadUInt32(); var nameRva = imports.ReadInt32(); imports.ReadUInt32();
                if (nameRva == 0) break;
                var text = executable.GetSectionData(nameRva).GetReader();
                var bytes = text.ReadBytes(Math.Min(256, text.RemainingBytes)); var end = Array.IndexOf(bytes, (byte)0);
                if (end < 0) throw new BadImageFormatException("Invalid dependency name.");
                var name = System.Text.Encoding.ASCII.GetString(bytes, 0, end);
                if (name.StartsWith("msvcp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("msvcr", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '.')))
                        throw new BadImageFormatException("Invalid runtime dependency name.");
                    names.Add(name);
                }
            }
        }
        return names.Select(name => Inspect(name, Path.Combine(directory, name), header.MajorLinkerVersion == 14 ? header.MinorLinkerVersion : 0)).ToArray();
    }
    private static ServerRuntimeLibrary Inspect(string name, string local, int minimumMinor)
    {
        var path = File.Exists(local) ? local : Path.Combine(Environment.SystemDirectory, name);
        try
        {
            for (var ancestor = Path.GetFullPath(path); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                if (Path.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    return new(name, path, false, "The runtime library uses a linked path.");
            if (!File.Exists(path)) return new(name, local, false, "Runtime library is missing. Check and repair server files.");
            using var input = File.OpenRead(path); using var image = new PEReader(input);
            if (image.PEHeaders.CoffHeader.Machine != Machine.Amd64) return new(name, path, false, "Runtime library is not a valid x64 library.");
            var version = FileVersionInfo.GetVersionInfo(path);
            var ready = name.Contains("120", StringComparison.OrdinalIgnoreCase) ? version.FileMajorPart == 12
                : version.FileMajorPart == 14 && version.FileMinorPart >= minimumMinor;
            return new(name, path, ready, ready ? "Compatible runtime library found." : "Runtime library version needs review. Check and repair server files.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
        { return new(name, path, false, "Runtime library could not be validated. Check and repair server files."); }
    }
}
