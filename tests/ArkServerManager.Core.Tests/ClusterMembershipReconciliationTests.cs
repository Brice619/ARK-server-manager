using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class ClusterMembershipReconciliationTests
{
    [Fact]
    public void ReconcilesWholeSelectionWithoutClaimingOperationSuccess()
    {
        var a = new ClusterMemberReview(Guid.NewGuid(), ArkEdition.ASE, "a", null);
        var b = new ClusterMemberReview(Guid.NewGuid(), ArkEdition.ASE, "b", null);
        var target = Guid.NewGuid();
        var edit = new ClusterMembershipEdit([a, b], target, true);
        Assert.Equal(ClusterMembershipCurrentState.OriginalMembershipPresent, ClusterMembershipReconciliation.Evaluate(edit, [b, a]));
        Assert.Equal(ClusterMembershipCurrentState.RequestedMembershipPresent, ClusterMembershipReconciliation.Evaluate(edit, [b with { ClusterId = target }, a with { ClusterId = target }]));
        Assert.Equal(ClusterMembershipCurrentState.Changed, ClusterMembershipReconciliation.Evaluate(edit, [a with { ClusterId = target }, b]));
        Assert.Equal(ClusterMembershipCurrentState.Changed, ClusterMembershipReconciliation.Evaluate(edit, [a]));
        Assert.Equal(ClusterMembershipCurrentState.Changed, ClusterMembershipReconciliation.Evaluate(edit, [a with { ClusterId = target, Directory = "replacement" }, b with { ClusterId = target }]));
    }
}
