using Microsoft.Win32;

namespace ArkServerManager.Core;

public sealed record InstallationSearchLocations(IReadOnlyList<string> Roots, IReadOnlyList<string> Findings);

public static class DefaultInstallationSearch
{
    public static InstallationSearchLocations Locate(string dedicatedFolder, IEnumerable<string>? configuredToolFolders = null)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = new HashSet<string>(comparison) { Path.GetFullPath(dedicatedFolder) };
        var steamFolders = new HashSet<string>(comparison);
        var findings = new List<string>();
        foreach (var folder in configuredToolFolders ?? []) roots.Add(Path.GetFullPath(folder));
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var registryPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (!string.IsNullOrWhiteSpace(registryPath)) steamFolders.Add(Path.GetFullPath(registryPath));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { findings.Add("Steam's registered location could not be read: " + error.Message); }
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFiles)) steamFolders.Add(Path.Combine(programFiles, "Steam"));
        }
        else if (OperatingSystem.IsLinux())
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            steamFolders.Add(Path.Combine(profile, ".local", "share", "Steam"));
            steamFolders.Add(Path.Combine(profile, ".steam", "steam"));
        }
        foreach (var steam in steamFolders.Where(Directory.Exists))
        {
            try { foreach (var library in InstallationDiscovery.SteamLibraryRoots(steam)) roots.Add(library); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { findings.Add("Steam libraries could not be read: " + error.Message); }
        }
        return new(roots.Where(Directory.Exists).ToArray(), findings);
    }

    public static async Task<DiscoveryResult> SearchAsync(string dedicatedFolder, IEnumerable<string>? configuredToolFolders = null,
        CancellationToken ct = default)
    {
        var locations = Locate(dedicatedFolder, configuredToolFolders);
        var result = await new InstallationDiscovery().SearchAsync(locations.Roots, ct);
        return result with
        {
            Findings = [.. locations.Findings, .. result.Findings,
                .. (result.Candidates.Count == 0 ? new[] { "No ARK installation was found in the automatic search locations. Choose a folder manually to evaluate it." } : Array.Empty<string>())]
        };
    }
}
