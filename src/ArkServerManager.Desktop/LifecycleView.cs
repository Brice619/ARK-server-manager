using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool lifecycleBusy, lifecycleAttempted, pendingLifecycleMemberOnly;
    private Guid lifecycleRequestId;
    private LifecycleRequest? pendingLifecycle;
    private string lifecycleNotice = "";
    private IReadOnlyList<MemberOperationResult> managedRuntimeStatus = [];

    private async Task RunLifecycleActionAsync(Guid serverId, LifecycleAction action, bool memberOnly)
    {
        if (client is null || lifecycleBusy || lifetime.IsCancellationRequested) return;
        lifecycleBusy = true;
        try
        {
            if (pendingLifecycle is null)
            {
                pendingLifecycle = new(serverId, action); pendingLifecycleMemberOnly = memberOnly;
                lifecycleRequestId = Guid.NewGuid(); lifecycleAttempted = false;
            }
            lifecycleNotice = "Connecting to server action…"; Render();
            BackgroundOperation job;
            if (!lifecycleAttempted)
            {
                lifecycleAttempted = true;
                job = await client.CallAsync<BackgroundOperation>(pendingLifecycleMemberOnly ? "lifecycle-execute-member" : "lifecycle-execute",
                    pendingLifecycle, TimeSpan.FromSeconds(8), lifetime.Token, lifecycleRequestId);
            }
            else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(lifecycleRequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            while (true)
            {
                if (job.Id != lifecycleRequestId || job.Kind != (pendingLifecycleMemberOnly ? "server-member-lifecycle" : "server-lifecycle")
                    || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<LifecycleOperationResult>();
                    if (result?.Members is null || result.Members.Count == 0 || !result.Members.Any(m => m.ServerId == pendingLifecycle.ServerId)
                        || result.Members.Any(m => m is null || !Enum.IsDefined(m.State))) throw new InvalidDataException();
                    var failures = result.Members.Where(m => !m.Success).ToArray();
                    lifecycleNotice = result.Success ? $"{pendingLifecycle.Action} completed for {result.Members.Count} server(s)."
                        : string.Join(" ", failures.Select(m => m.Error ?? "A server action failed."));
                    pendingLifecycle = null;
                    await RefreshAsync(); break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { lifecycleNotice = job.Error ?? "Server action did not complete. Refresh server status before trying again."; pendingLifecycle = null; break; }
                await RefreshLifecycleDisplayAsync();
                lifecycleNotice = job.Progress?.Message ?? "Server action is in progress…";
                if(pendingLifecycle.Action is LifecycleAction.Start or LifecycleAction.Restart && managedRuntimeStatus.Any(s=>s.ServerId==pendingLifecycle.ServerId&&s.State==ServerState.Starting))
                    lifecycleNotice="Starting ARK and waiting for its administration connection. Map and mod loading can take several minutes.";
                Render();
                await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(lifecycleRequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException)
        {
            if (!lifecycleAttempted)
            {
                pendingLifecycle = null;
                lifecycleNotice = "Startup preparation failed: " + error.Message + " The server action was not sent.";
            }
            else lifecycleNotice = "Server action result unknown. Check the existing action to reconnect; it will not be sent again automatically.";
        }
        finally { lifecycleBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private async Task RefreshLifecycleDisplayAsync()
    {
        if (client is null || lifetime.IsCancellationRequested) return;
        try
        {
            var status = await client.CallAsync<List<MemberOperationResult>>("lifecycle-status", null,
                TimeSpan.FromSeconds(2), lifetime.Token);
            if (status is null || status.Any(s => s is null || s.ServerId == Guid.Empty || !Enum.IsDefined(s.State))
                || status.Select(s => s.ServerId).Distinct().Count() != status.Count)
                throw new InvalidDataException("Unexpected server status.");
            managedRuntimeStatus = status;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or JsonException)
        {
            // A display refresh cannot turn an accepted lifecycle job into an unknown
            // save/start outcome or cause it to be submitted again.
        }
    }
}
