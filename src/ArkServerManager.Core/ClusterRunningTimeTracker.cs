namespace ArkServerManager.Core;

/// <summary>Counts a cluster once while any ready member is alive, including non-overlapping member runs.</summary>
public sealed class ClusterRunningTimeTracker(TimeProvider? time = null)
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, (Guid? Cluster, ServerState State)> members = [];
    private readonly Dictionary<Guid, RunningTimeTracker> clusters = [];

    public void Observe(Guid serverId, Guid? clusterId, ServerState state)
    {
        if (serverId == Guid.Empty || clusterId == Guid.Empty || !Enum.IsDefined(state))
            throw new ArgumentException("Choose valid member state identities.");
        lock (gate)
        {
            var previous = members.GetValueOrDefault(serverId).Cluster;
            members[serverId] = (clusterId, state);
            if (previous is Guid old && previous != clusterId) Refresh(old);
            if (clusterId is Guid current) Refresh(current);
        }
    }

    public TimeSpan Runtime(Guid clusterId)
    {
        if (clusterId == Guid.Empty) throw new ArgumentException("Choose a valid cluster.");
        lock (gate) return clusters.TryGetValue(clusterId, out var clock) ? clock.Runtime : TimeSpan.Zero;
    }

    private void Refresh(Guid clusterId)
    {
        if (!clusters.TryGetValue(clusterId, out var clock)) clusters.Add(clusterId, clock = new(time));
        var active = members.Values.Any(member => member.Cluster == clusterId
            && member.State is ServerState.Running or ServerState.Stopping);
        clock.Transition(active ? ServerState.Running : ServerState.Stopped);
    }

    public bool AllRunning(Guid clusterId, IReadOnlyList<Guid> serverIds)
    {
        if (clusterId == Guid.Empty || serverIds.Any(id => id == Guid.Empty) || serverIds.Distinct().Count() != serverIds.Count)
            throw new ArgumentException("Choose valid distinct cluster members.");
        lock (gate) return serverIds.Count > 0 && serverIds.All(id => members.TryGetValue(id, out var member)
            && member.Cluster == clusterId && member.State == ServerState.Running);
    }
}
