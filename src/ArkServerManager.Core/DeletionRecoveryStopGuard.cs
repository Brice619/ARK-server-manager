namespace ArkServerManager.Core;

/// <summary>Checks original and staged process locations, including an original directory no longer present.</summary>
public sealed class DeletionRecoveryStopGuard(Func<CancellationToken, Task<ServerProcessScan>> scan)
{
    public DeletionRecoveryStopGuard() : this(new ServerProcessDiscovery().ScanAsync) { }

    public async Task<bool> VerifyAsync(ServerDeletionRecord record, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return false;
        var locations = new[] { record.OriginalDirectory, record.StagedDirectory };
        foreach (var path in locations)
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        var started = DateTimeOffset.UtcNow;
        var observed = await scan(ct);
        if (!observed.NativeScanComplete || observed.MeasuredAt < started || observed.MeasuredAt > DateTimeOffset.UtcNow) return false;
        return !observed.Candidates.Any(candidate => locations.Any(path =>
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Directory)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
    }
}
