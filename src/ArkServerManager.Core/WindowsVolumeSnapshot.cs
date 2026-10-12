using System.Text.RegularExpressions;

namespace ArkServerManager.Core;

/// <summary>Validates mapping of local state paths into an explicitly identified Windows shadow device.</summary>
public static class WindowsVolumeSnapshot
{
    public static string ResolveDirectory(string sourceDirectory, string deviceObject)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("VSS requires Windows.");
        if (deviceObject is null || !Regex.IsMatch(deviceObject,
            @"^\\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy[1-9][0-9]*$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Unexpected volume shadow device.");
        var source = Path.GetFullPath(sourceDirectory);
        var volume = Path.GetPathRoot(source)!;
        if (!Regex.IsMatch(volume, @"^[A-Za-z]:\\$", RegexOptions.CultureInvariant))
            throw new NotSupportedException("Automatic VSS capture requires a local drive path.");
        var relative = Path.GetRelativePath(volume, source);
        if (relative == "." || Path.IsPathRooted(relative) || relative.Split('\\').Any(part => part is ".." or "."))
            throw new InvalidDataException("Select a state directory inside the volume.");
        return deviceObject + "\\" + relative;
    }

    public static IReadOnlyDictionary<string, string> ResolveDirectories(IReadOnlyList<string> sources, string deviceObject)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) throw new ArgumentException("Select at least one state directory.");
        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? volume = null;
        foreach (var item in sources)
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item));
            var directory = ResolveDirectory(source, deviceObject);
            var currentVolume = Path.GetPathRoot(source)!;
            if (volume is not null && !string.Equals(volume, currentVolume, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Use a multi-volume snapshot response to map directories from different volumes.");
            volume = currentVolume;
            if (!mapped.TryAdd(source, directory)) throw new ArgumentException("State directories must be distinct.");
        }
        return mapped;
    }
}
