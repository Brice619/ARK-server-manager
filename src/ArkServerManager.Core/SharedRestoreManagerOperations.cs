using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record SharedRestoreRequest(Guid ClusterId, long RuntimeHour, Guid? CoordinatedCaptureId, bool Confirmed);
public sealed record SharedRollbackRequest(Guid ClusterId, Guid RestoreOperationId, bool Confirmed);

public sealed class SharedRestoreManagerOperations(BackgroundOperations operations,
    ServerLifecycleCoordinator lifecycle, string root,
    Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped)
{
    public BackgroundOperation Start(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<SharedRestoreRequest>() ?? throw new ArgumentException("Choose shared restore data.");
        if (!selection.Confirmed || selection.ClusterId == Guid.Empty || selection.RuntimeHour < 1 || selection.CoordinatedCaptureId == Guid.Empty)
            throw new ArgumentException("Confirm the selected shared restore point.");
        return operations.Submit(request.Id, "shared-transfer-restore", request.Payload, async (progress, ct) =>
        {
            SharedTransferRestoreRecord? result = null;
            progress(new("Verifying all cluster maps are stopped before restoring shared transfer data."));
            await lifecycle.WithStoppedClusterAsync(selection.ClusterId, root, async (catalog, token) =>
            {
                await ClusterRestoreRecords.RequireConsistentAsync(catalog, selection.ClusterId, root, token);
                string path; SnapshotCapture? capture = null;
                if (selection.CoordinatedCaptureId is Guid captureId)
                {
                    var selected = await CoordinatedArchiveSelection.ResolveAsync(catalog, root,
                        new(selection.ClusterId, selection.RuntimeHour, null), token);
                    if (selected.Point.Capture.Id != captureId) throw new InvalidDataException("The selected coordinated capture changed.");
                    path = selected.Path; capture = selected.Point.Capture;
                }
                else
                {
                    var history = await AutomaticBackupService.ReadRecordedClusterAsync(catalog, selection.ClusterId, root, token);
                    var point = history.History.SingleOrDefault(h => h.RuntimeHour == selection.RuntimeHour)
                        ?? throw new ArgumentException("This shared restore point is no longer recorded.");
                    path = Path.Combine(ManagedClusterStorage.Resolve(catalog, selection.ClusterId, root).Backups, point.FileName);
                }
                result = await SharedTransferRestore.RestoreAsync(catalog, selection.ClusterId, root, path, selection.RuntimeHour,
                    request.Id, capture, token);
            }, verifyStopped, ct);
            return result ?? throw new InvalidOperationException("Shared restoration was not authorized.");
        });
    }
    public BackgroundOperation StartRollback(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<SharedRollbackRequest>() ?? throw new ArgumentException("Choose an interrupted shared restore.");
        if (!selection.Confirmed || selection.ClusterId == Guid.Empty || selection.RestoreOperationId == Guid.Empty)
            throw new ArgumentException("Confirm shared restore recovery.");
        return operations.Submit(request.Id, "shared-transfer-rollback", request.Payload, async (progress, ct) =>
        {
            SharedTransferRestoreRecord? result = null;
            progress(new("Verifying the cluster is stopped before recovering previous transfer data."));
            await lifecycle.WithStoppedClusterAsync(selection.ClusterId, root, async (catalog, token) =>
            {
                await ClusterRestoreRecords.RequireConsistentAsync(catalog, selection.ClusterId, root, token);
                await SharedTransferRestore.RollbackAsync(catalog, selection.ClusterId, root, selection.RestoreOperationId, token);
                result = (await SharedTransferRestore.ReadAsync(catalog, selection.ClusterId, root, token))
                    .Single(r => r.OperationId == selection.RestoreOperationId);
            }, verifyStopped, ct);
            return result ?? throw new InvalidOperationException("Shared recovery was not authorized.");
        });
    }
}

