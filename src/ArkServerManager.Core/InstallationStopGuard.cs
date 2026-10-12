namespace ArkServerManager.Core;

public enum InstallationStopState { Stopped, Running, Unverified }
public sealed record InstallationStopCheck(string Directory, InstallationStopState State,
    IReadOnlyList<ServerProcessCandidate> Processes, IReadOnlyList<string> Findings, DateTimeOffset MeasuredAt);

/// <summary>Observed stop checks. Callers must also hold host lifecycle locks throughout their file operation.</summary>
public sealed class InstallationStopGuard(Func<CancellationToken, Task<ServerProcessScan>> scan)
{
    public InstallationStopGuard() : this(new ServerProcessDiscovery().ScanAsync) { }

    public async Task<InstallationStopCheck> CheckAsync(string directory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        InstallationStopCheck Unknown(string message) => new(root, InstallationStopState.Unverified, [], [message], DateTimeOffset.UtcNow);
        try
        {
            if (!Directory.Exists(root)) return Unknown("The installation folder is missing or inaccessible.");
            root = Path.TrimEndingDirectorySeparator(WindowsNativePath.ExpandExisting(root));
            for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                    return Unknown("The installation uses a linked path; native process identity cannot be matched reliably.");
            root = Path.TrimEndingDirectorySeparator(WindowsNativePath.ResolveFinalExisting(root));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Unknown("The installation path could not be inspected: " + error.Message); }
        var installed = new InstallationDiscovery().Evaluate(root);
        var platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : null;
        if (!installed.Candidates.Any(c => c.Platform == platform || OperatingSystem.IsLinux() && c.Platform == "Windows"))
            return Unknown("No supported native server executable was found to evaluate running processes.");
        var observed = await scan(ct);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matching = observed.Candidates.Where(c => Path.TrimEndingDirectorySeparator(Path.GetFullPath(c.Directory)).Equals(root, comparison)).ToArray();
        if (!observed.NativeScanComplete)
            return new(root, InstallationStopState.Unverified, matching,
                [.. observed.Findings, .. observed.UnresolvedProcesses.Select(process =>
                    $"Process {process.ProcessId} ({process.Name ?? "unknown name"}): {process.Reason}"),
                    "Unresolved processes prevent confirmation that this installation is stopped."], observed.MeasuredAt);
        return new(root, matching.Length == 0 ? InstallationStopState.Stopped : InstallationStopState.Running,
            matching, observed.Findings, observed.MeasuredAt);
    }

    public async Task<InstallationStopCheck> EnsureStoppedAsync(string directory,
        Func<InstallationStopCheck, CancellationToken, Task<bool>> requestShutdown, CancellationToken ct = default)
    {
        var before = await CheckAsync(directory, ct);
        if (before.State != InstallationStopState.Running) return before;
        if (!await requestShutdown(before, ct))
            return before with { Findings = [.. before.Findings, "Shutdown was declined or did not complete; no transfer is allowed."] };
        // Approval or command delivery alone is not evidence of shutdown.
        return await CheckAsync(directory, ct);
    }
}
