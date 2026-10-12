using System.Text.RegularExpressions;

namespace ArkServerManager.Core;

public sealed record InstallationCandidate(string Directory, ArkEdition Edition, string Executable,
    string Platform, IReadOnlyList<string> ConfigurationFiles, IReadOnlyList<string> WorldSaves,
    IReadOnlyList<string> ModDirectories, IReadOnlyList<string> Findings);
public sealed record DiscoveryResult(IReadOnlyList<InstallationCandidate> Candidates, IReadOnlyList<string> Findings);
public sealed record InstallationEvaluationRequest(string Directory);

/// <summary>Read-only discovery. An executable on disk is a candidate, not proof of runtime compatibility.</summary>
public sealed partial class InstallationDiscovery
{
    internal static readonly (ArkEdition Edition, string Path, string Platform)[] Executables =
    [
        (ArkEdition.ASA, "ShooterGame/Binaries/Win64/ArkAscendedServer.exe", "Windows"),
        (ArkEdition.ASE, "ShooterGame/Binaries/Win64/ShooterGameServer.exe", "Windows"),
        (ArkEdition.ASE, "ShooterGame/Binaries/Linux/ShooterGameServer", "Linux"),
        (ArkEdition.ASE, "ShooterGame/Binaries/Linux/ShooterGame", "Linux")
    ];

    public DiscoveryResult Evaluate(string directory)
    {
        var findings = new List<string>();
        var candidates = new List<InstallationCandidate>();
        var root = Path.GetFullPath(directory);
        if (!System.IO.Directory.Exists(root)) return new([], ["The selected folder does not exist."]);
        foreach (var item in Executables)
        {
            if (candidates.Any(c => c.Edition == item.Edition && c.Platform == item.Platform)) continue;
            var executable = Path.Combine(root, item.Path);
            if (!File.Exists(executable)) continue;
            var issues = new List<string>();
            try
            {
                if (new FileInfo(executable).Length == 0) issues.Add("The server executable is empty.");
                if (item.Platform == "Windows" && OperatingSystem.IsLinux()) issues.Add("This Windows server needs a validated compatibility environment on Linux.");
                if (item.Platform == "Linux" && OperatingSystem.IsWindows()) issues.Add("This Linux executable cannot run natively on Windows.");
                var saved = Path.Combine(root, "ShooterGame", "Saved");
                var config = ListFiles(Path.Combine(saved, "Config", item.Platform + "Server"), "*.ini", issues);
                var worlds = ListFiles(Path.Combine(saved, "SavedArks"), "*.ark", issues);
                var alternate = ListFiles(Path.Combine(saved, "SavedArksLocal"), "*.ark", issues);
                var mods = ListDirectories(Path.Combine(root, "ShooterGame", "Content", "Mods"), issues);
                if (config.Count == 0) issues.Add("No configuration files found; a fresh installation may not have created them yet.");
                issues.Add("Dependencies, edition version, map availability, mod compatibility, and healthy startup still need validation.");
                candidates.Add(new(root, item.Edition, executable, item.Platform, config, [.. worlds, .. alternate], mods, issues));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { findings.Add($"Could not evaluate {root}: {error.Message}"); }
        }
        if (candidates.Count == 0) findings.Add("No recognized ARK dedicated-server executable was found in this folder.");
        return new(candidates, findings);
    }

    public async Task<DiscoveryResult> SearchAsync(IEnumerable<string> searchRoots, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<InstallationCandidate>();
            var findings = new List<string>();
            var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var supplied in searchRoots)
            {
                var queue = new Queue<(string Path, int Depth)>();
                queue.Enqueue((Path.GetFullPath(supplied), 0));
                while (queue.TryDequeue(out var entry))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!visited.Add(entry.Path) || !System.IO.Directory.Exists(entry.Path)) continue;
                    try
                    {
                        if ((File.GetAttributes(entry.Path) & FileAttributes.ReparsePoint) != 0) continue;
                        var evaluated = Evaluate(entry.Path);
                        if (evaluated.Candidates.Count > 0) { results.AddRange(evaluated.Candidates); continue; }
                        if (entry.Depth >= 4) continue;
                        foreach (var child in System.IO.Directory.EnumerateDirectories(entry.Path))
                            queue.Enqueue((child, entry.Depth + 1));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { findings.Add($"Could not search {entry.Path}: {error.Message}"); }
                }
            }
            return new DiscoveryResult(results, findings);
        }, cancellationToken);
    }

    public static IReadOnlyList<string> SteamLibraryRoots(string steamDirectory)
    {
        var steam = Path.GetFullPath(steamDirectory);
        var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        { Path.Combine(steam, "steamapps", "common") };
        var libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraries)) return roots.ToArray();
        var text = File.ReadAllText(libraries);
        // Modern Steam library entries have a quoted path field. Legacy numeric entries are supported too.
        foreach (Match match in LibraryPath().Matches(text))
        {
            var path = match.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
            if (Path.IsPathFullyQualified(path)) roots.Add(Path.Combine(path, "steamapps", "common"));
        }
        return roots.ToArray();
    }

    [GeneratedRegex("\"(?:path|[0-9]+)\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();

    private static IReadOnlyList<string> ListFiles(string path, string pattern, List<string> findings)
    {
        try { return System.IO.Directory.Exists(path) ? System.IO.Directory.GetFiles(path, pattern) : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { findings.Add($"Could not read {path}: {error.Message}"); return []; }
    }

    private static IReadOnlyList<string> ListDirectories(string path, List<string> findings)
    {
        try { return System.IO.Directory.Exists(path) ? System.IO.Directory.GetDirectories(path) : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { findings.Add($"Could not read {path}: {error.Message}"); return []; }
    }
}
