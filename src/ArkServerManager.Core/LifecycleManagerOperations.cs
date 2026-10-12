using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record LifecycleRequest(Guid ServerId, LifecycleAction Action);

/// <summary>Host request routing. Action results include per-member failures even when the job completed normally.</summary>
public sealed class LifecycleManagerOperations(ServerLifecycleCoordinator coordinator, BackgroundOperations operations,
    Func<CancellationToken, Task>? afterStopAll = null)
{
    public async Task<object> HandleAsync(ManagerRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Operation == "lifecycle-status") return await coordinator.StatusAsync(ct);
        if (request.Operation == "lifecycle-stop-all")
        {
            if (request.Payload is not null) throw new ArgumentException("Stopping all owned servers takes no selection.");
            return operations.Submit(request.Id, "server-shutdown", null, async (progress, token) =>
            {
                progress(new("Saving and stopping your managed servers."));
                var stopped = await coordinator.StopAllAsync(token);
                if (afterStopAll is not null) await afterStopAll(token);
                return stopped;
            });
        }
        var memberOnly = request.Operation == "lifecycle-execute-member";
        if (!memberOnly && request.Operation != "lifecycle-execute") throw new ArgumentException("Unknown server lifecycle operation.");
        LifecycleRequest selection;
        try { selection = request.Payload?.Deserialize<LifecycleRequest>() ?? throw new ArgumentException("Choose a server and action."); }
        catch (JsonException error) { throw new ArgumentException("Server action details are invalid.", error); }
        if (selection.ServerId == Guid.Empty || !Enum.IsDefined(selection.Action))
            throw new ArgumentException("Choose a server and supported action.");
        return operations.Submit(request.Id, memberOnly ? "server-member-lifecycle" : "server-lifecycle", request.Payload, async (progress, token) =>
        {
            progress(new(selection.Action switch
            {
                LifecycleAction.Start => memberOnly ? "Starting the selected map." : "Starting the server or cluster.",
                LifecycleAction.Stop => memberOnly ? "Saving and stopping the selected map." : "Saving and stopping the server or cluster.",
                _ => memberOnly ? "Saving and restarting the selected map." : "Saving and restarting the server or cluster."
            }));
            // Use the host job token so a disconnected desktop cannot interrupt a save/start halfway through.
            return memberOnly
                ? await coordinator.ExecuteMemberAsync(selection.ServerId, selection.Action, token)
                : await coordinator.ExecuteAsync(selection.ServerId, selection.Action, token);
        });
    }
}
