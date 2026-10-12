namespace ArkServerManager.Core;

public enum ClusterMembershipCurrentState { RequestedMembershipPresent, OriginalMembershipPresent, Changed }

/// <summary>Reports current catalog state without claiming an unavailable operation succeeded or resubmitting it.</summary>
public static class ClusterMembershipReconciliation
{
    public static ClusterMembershipCurrentState Evaluate(ClusterMembershipEdit edit, IReadOnlyList<ClusterMemberReview> current)
    {
        if (edit.Expected is null || edit.Expected.Count == 0 || edit.Expected.Any(member => member is null)
            || edit.Expected.Select(member => member.ServerId).Distinct().Count() != edit.Expected.Count
            || current.Any(member => member is null) || current.Select(member => member.ServerId).Distinct().Count() != current.Count)
            throw new InvalidDataException("Membership review contains invalid server identities.");
        var selected = edit.Expected.Select(expected => current.SingleOrDefault(member => member.ServerId == expected.ServerId)).ToArray();
        if (selected.Any(member => member is null)) return ClusterMembershipCurrentState.Changed;
        for (var index = 0; index < selected.Length; index++)
            if (selected[index]!.Edition != edit.Expected[index].Edition || selected[index]!.Directory != edit.Expected[index].Directory)
                return ClusterMembershipCurrentState.Changed;
        if (selected.All(member => member!.ClusterId == edit.TargetClusterId)) return ClusterMembershipCurrentState.RequestedMembershipPresent;
        if (selected.SequenceEqual(edit.Expected)) return ClusterMembershipCurrentState.OriginalMembershipPresent;
        return ClusterMembershipCurrentState.Changed;
    }
}
