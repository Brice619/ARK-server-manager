using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record WorldRestoreRequest(Guid ServerId, long RuntimeHour, bool Restart, bool Confirmed);
public sealed record WorldRestoreOperationResult(Guid ServerId, long RuntimeHour, RestoreResult Restore);
public sealed record CoordinatedWorldRestoreRequest(Guid ClusterId, Guid ServerId, long RuntimeHour,
    Guid CaptureId, bool Restart, bool Confirmed);
public sealed record WorldRollbackRequest(Guid ServerId, Guid RestoreOperationId, bool Confirmed);
public sealed record WorldRollbackResult(Guid ServerId, Guid RestoreOperationId, WorldRestorePhase Phase);

public sealed class WorldRestoreManagerOperations(CatalogRepository repository, BackgroundOperations operations,
    ServerLifecycleCoordinator lifecycle, string managementRoot, Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped)
{
    private async Task RequireClusterConsistentAsync(ServerDefinition server, CancellationToken ct)
    {
        if (server.ClusterId is Guid clusterId)
            await ClusterRestoreRecords.RequireConsistentAsync(await repository.ReadAsync(ct), clusterId, managementRoot, ct);
    }

    public BackgroundOperation StartRollback(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<WorldRollbackRequest>()
            ?? throw new ArgumentException("Choose an interrupted world restore.");
        if (!selection.Confirmed || selection.ServerId == Guid.Empty || selection.RestoreOperationId == Guid.Empty)
            throw new ArgumentException("Confirm rollback of the selected interrupted restore.");
        return operations.Submit(request.Id, "world-restore-rollback", request.Payload, async (progress, ct) =>
        {
            var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == selection.ServerId);
            progress(new("Checking that only the selected map is stopped before restoring its previous world state."));
            await lifecycle.WithStoppedMemberAsync(server,
                async token => { await RequireClusterConsistentAsync(server, token); await WorldRestoreRecovery.RollbackInterruptedAsync(server, selection.RestoreOperationId, token); }, verifyStopped, ct);
            var record = (await WorldRestoreRecovery.ReadAsync(server, ct)).Single(r => r.OperationId == selection.RestoreOperationId);
            return new WorldRollbackResult(server.Id, record.OperationId, record.Phase);
        });
    }

    public BackgroundOperation StartCoordinated(ManagerRequest request, string managementRoot)
    {
        var selection = request.Payload?.Deserialize<CoordinatedWorldRestoreRequest>()
            ?? throw new ArgumentException("Choose a coordinated map restore point.");
        if (!selection.Confirmed || selection.ServerId == Guid.Empty || selection.ClusterId == Guid.Empty
            || selection.CaptureId == Guid.Empty || selection.RuntimeHour < 1)
            throw new ArgumentException("Confirm the selected map and coordinated capture.");
        return operations.Submit(request.Id, "coordinated-world-restore", request.Payload, async (progress, ct) =>
        {
            var catalog = await repository.ReadAsync(ct);
            var selected = await CoordinatedArchiveSelection.ResolveAsync(catalog, managementRoot,
                new(selection.ClusterId, selection.RuntimeHour, selection.ServerId), ct);
            if (selected.Point.Capture.Id != selection.CaptureId || selected.Server is null)
                throw new InvalidDataException("The selected coordinated capture changed.");
            var server = selected.Server;
            var service = new WorldRestoreService(async token =>
            {
                var current = (await repository.ReadAsync(token)).Servers.Single(s => s.Id == server.Id);
                if (current.Directory != server.Directory || current.Edition != server.Edition || current.Map != server.Map
                    || current.ClusterId != server.ClusterId) throw new InvalidOperationException("The selected map changed before restoration.");
                progress(new("Saving and stopping only the selected map."));
                return (await lifecycle.ExecuteMemberAsync(server.Id, LifecycleAction.Stop, token)).Success;
            }, async token =>
            {
                var started = await lifecycle.ExecuteMemberAsync(server.Id, LifecycleAction.Start, token);
                if (!started.Success || started.Members.Count != 1 || started.Members[0].State != ServerState.Running)
                    throw new InvalidOperationException("The restored map did not reach verified running state.");
            }, (change, token) => lifecycle.WithStoppedMemberAsync(server, async inner => { await RequireClusterConsistentAsync(server, inner); await change(inner); }, verifyStopped, token));
            progress(new("Verifying the coordinated world archive before stopping the selected map."));
            var restored = await service.RestoreAsync(server, selected.Path, selection.RuntimeHour, request.Id,
                selection.Restart, ct, selected.Point.Capture);
            return new WorldRestoreOperationResult(server.Id, selection.RuntimeHour, restored);
        });
    }

    public BackgroundOperation Start(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<WorldRestoreRequest>() ?? throw new ArgumentException("Choose a world restore point.");
        if (selection.ServerId == Guid.Empty || selection.RuntimeHour < 1 || !selection.Confirmed)
            throw new ArgumentException("Confirm the selected map and world restore point.");
        return operations.Submit(request.Id, "server-world-restore", request.Payload, async (progress, ct) =>
        {
            var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == selection.ServerId);
            var history = await AutomaticBackupService.ReadRecordedAsync(server, ct);
            var point = history.History.SingleOrDefault(entry => entry.RuntimeHour == selection.RuntimeHour)
                ?? throw new InvalidOperationException("The selected restore point is no longer recorded.");
            var path = Path.Combine(server.Directory, "backups", point.FileName);
            var service = new WorldRestoreService(async token =>
            {
                var current = (await repository.ReadAsync(token)).Servers.Single(s => s.Id == server.Id);
                if (current.Directory != server.Directory || current.Edition != server.Edition || current.Map != server.Map || current.ClusterId != server.ClusterId)
                    throw new InvalidOperationException("The selected installation changed before restoration.");
                progress(new("Saving and stopping only the selected map."));
                return (await lifecycle.ExecuteMemberAsync(server.Id, LifecycleAction.Stop, token)).Success;
            }, async token =>
            {
                progress(new("Starting the restored map."));
                var started = await lifecycle.ExecuteMemberAsync(server.Id, LifecycleAction.Start, token);
                if (!started.Success || started.Members.Count != 1 || started.Members[0].State != ServerState.Running)
                    throw new InvalidOperationException("The restored map did not reach verified running state.");
            }, async (change, token) =>
            {
                progress(new("Replacing world and player files; keeping settings and shared cluster data."));
                await lifecycle.WithStoppedMemberAsync(server, async inner => { await RequireClusterConsistentAsync(server, inner); await change(inner); }, verifyStopped, token);
            });
            progress(new("Verifying the selected world archive before stopping the map."));
            var restored = await service.RestoreAsync(server, path, selection.RuntimeHour, request.Id, selection.Restart, ct);
            return new WorldRestoreOperationResult(server.Id, selection.RuntimeHour, restored);
        });
    }
}

