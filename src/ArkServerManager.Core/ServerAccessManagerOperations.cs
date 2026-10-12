using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ServerAccessSave(ServerAccessChange Change, SaveChoice Choice, bool Confirmed)
{
    public override string ToString() => "Server access save; passwords redacted";
}
public sealed record ServerAccessSaveResult(Guid ServerId, SettingsSaveResult Save);
public sealed record ServerAccessDiscard(Guid ServerId, Guid PendingId, bool Confirmed);
public sealed record ServerAccessDiscardResult(Guid ServerId, bool Discarded);

public sealed class ServerAccessManagerOperations(CatalogRepository repository, ServerAccessChanges changes, BackgroundOperations operations,
    Func<Func<IReadOnlyList<MemberOperationResult>, CancellationToken, Task>, CancellationToken, Task> withStatusScope,
    Func<ServerDefinition, CancellationToken, Task> requireStopped,
    Func<Guid, LifecycleAction, CancellationToken, Task<LifecycleOperationResult>> lifecycle,
    Func<Guid, CancellationToken, Task>? requireNoConflictingConfiguration = null)
{
    public BackgroundOperation Start(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<ServerAccessSave>() ?? throw new ArgumentException("Review server access settings.");
        if (!edit.Confirmed || edit.Change?.Expected is null || !Enum.IsDefined(edit.Choice) || edit.Choice == SaveChoice.Cancel)
            throw new ArgumentException("Confirm when to apply the password changes.");
        var id = edit.Change.Expected.ServerId;
        return operations.Submit(request.Id, "server-access-save", request.Payload, async (progress, ct) =>
        {
            var running = false;
            progress(new("Checking the selected server and saving its password changes."));
            await withStatusScope(async (statuses, token) =>
            {
                var status = statuses.Single(s => s.ServerId == id);
                if (status.State is ServerState.Starting or ServerState.Stopping or ServerState.Recovering)
                    throw new InvalidOperationException("Wait for the current server action before saving access settings.");
                running = status.Managed && status.State == ServerState.Running;
                var server = (await repository.ReadAsync(token)).Servers.Single(s => s.Id == id);
                if (requireNoConflictingConfiguration is not null) await requireNoConflictingConfiguration(id, token);
                if (!running) await requireStopped(server, token);
                await changes.QueueAsync(edit.Change, token);
                if (!running) await changes.ApplyBeforeStartAsync(id, token);
            }, ct);
            if (!running) return new ServerAccessSaveResult(id, new(true, false, false, "Access settings saved."));
            if (edit.Choice == SaveChoice.SaveForNextStart)
                return new ServerAccessSaveResult(id, new(true, true, false, "Password changes saved for the next start."));
            progress(new("Saving and stopping only the selected map."));
            if (!(await lifecycle(id, LifecycleAction.Stop, ct)).Success)
                return new ServerAccessSaveResult(id, new(true, true, false, "The server did not stop. Password changes remain saved for its next start."));
            progress(new("Starting the selected map with its saved access settings."));
            if (!(await lifecycle(id, LifecycleAction.Start, ct)).Success)
                throw new InvalidOperationException("The map could not restart. Its saved access change or current configuration needs review.");
            return new ServerAccessSaveResult(id, new(true, false, true, "Access settings saved and map restarted."));
        });
    }
    public BackgroundOperation StartDiscard(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<ServerAccessDiscard>() ?? throw new ArgumentException("Choose a saved password change.");
        if (!edit.Confirmed || edit.ServerId == Guid.Empty || edit.PendingId == Guid.Empty)
            throw new ArgumentException("Confirm cancellation of the reviewed password change.");
        return operations.Submit(request.Id, "server-access-discard", request.Payload, async (progress, ct) =>
        {
            progress(new("Checking the saved password change before cancellation."));
            await withStatusScope(async (statuses, token) =>
            {
                if (!statuses.Any(s => s.ServerId == edit.ServerId)) throw new ArgumentException("The server no longer exists.");
                await changes.DiscardAsync(edit.ServerId, edit.PendingId, true, token);
            }, ct);
            return new ServerAccessDiscardResult(edit.ServerId, true);
        });
    }
}
