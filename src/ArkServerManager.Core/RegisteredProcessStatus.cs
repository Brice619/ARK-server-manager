namespace ArkServerManager.Core;

public enum RegisteredProcessState { Unknown, Detected, NotDetected }
public sealed record RegisteredProcessObservation(Guid ServerId, RegisteredProcessState State, int ProcessCount);
public sealed record RegisteredProcessStatus(DateTimeOffset MeasuredAt, IReadOnlyList<RegisteredProcessObservation> Servers)
{
    /// <summary>Process presence is an observation, not proof of readiness or manager ownership.</summary>
    public static RegisteredProcessStatus FromScan(CatalogDocument catalog, ServerProcessScan scan, bool linux)
    {
        var comparison = linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return new(scan.MeasuredAt, catalog.Servers.Select(server =>
        {
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.Directory));
            var count = scan.Candidates.Count(candidate => candidate.Edition == server.Edition
                && string.Equals(directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Directory)), comparison));
            var state = count > 0 ? RegisteredProcessState.Detected
                : scan.NativeScanComplete && !(linux && server.Edition == ArkEdition.ASA)
                    ? RegisteredProcessState.NotDetected : RegisteredProcessState.Unknown;
            return new RegisteredProcessObservation(server.Id, state, count);
        }).ToArray());
    }
}
