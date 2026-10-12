using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ClusterRestoreRecoveryRequest(Guid ClusterId, Guid RestoreOperationId, bool Confirmed);

public sealed class ClusterRestoreManagerOperations(BackgroundOperations operations, ServerLifecycleCoordinator lifecycle,
    string root, Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped)
{
    public BackgroundOperation Start(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<CoordinatedClusterRestoreRequest>() ?? throw new ArgumentException("Choose a cluster restore point.");
        if (!selection.Confirmed || selection.ClusterId == Guid.Empty || selection.CaptureId == Guid.Empty || selection.RuntimeHour < 1)
            throw new ArgumentException("Confirm the cluster restore point.");
        return operations.Submit(request.Id, "cluster-world-restore", request.Payload, async (progress, ct) =>
        {
            ClusterRestoreRecord? result = null;
            progress(new("Verifying every cluster map is stopped and checking all restore archives."));
            await lifecycle.WithStoppedClusterAsync(selection.ClusterId, root, async (catalog, token) =>
            {
                await ClusterRestoreStaging.StageAsync(catalog, root, selection, request.Id, token);
                progress(new("Replacing all cluster worlds and shared data; settings are preserved."));
                result = await ClusterRestoreReplacement.ReplaceAsync(catalog, selection.ClusterId, root, request.Id, token);
            }, verifyStopped, ct);
            return result ?? throw new InvalidOperationException("Cluster restoration was not authorized.");
        });
    }
    public BackgroundOperation Recover(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<ClusterRestoreRecoveryRequest>() ?? throw new ArgumentException("Choose a cluster restore record.");
        if (!selection.Confirmed || selection.ClusterId == Guid.Empty || selection.RestoreOperationId == Guid.Empty)
            throw new ArgumentException("Confirm cluster restore recovery.");
        return operations.Submit(request.Id, "cluster-restore-recovery", request.Payload, async (progress, ct) =>
        {
            ClusterRestoreRecord? result = null;
            progress(new("Verifying the cluster is stopped before restoring its previous state."));
            await lifecycle.WithStoppedClusterAsync(selection.ClusterId, root, async (catalog, token) =>
            {
                var record = (await ClusterRestoreRecords.ReadAsync(catalog, selection.ClusterId, root, token))
                    .Single(r => r.OperationId == selection.RestoreOperationId);
                if (record.Phase is ClusterRestorePhase.Preparing or ClusterRestorePhase.Staged
                    || record.Phase == ClusterRestorePhase.RolledBack && record.OriginalFolders is null)
                {
                    await ClusterRestoreStaging.AbandonAsync(catalog, selection.ClusterId, root, selection.RestoreOperationId, token);
                    result = (await ClusterRestoreRecords.ReadAsync(catalog, selection.ClusterId, root, token)).Single(r => r.OperationId == record.OperationId);
                }
                else result = await ClusterRestoreRecovery.RollbackAsync(catalog, selection.ClusterId, root, selection.RestoreOperationId, token);
            }, verifyStopped, ct);
            return result ?? throw new InvalidOperationException("Cluster recovery was not authorized.");
        });
    }
}
