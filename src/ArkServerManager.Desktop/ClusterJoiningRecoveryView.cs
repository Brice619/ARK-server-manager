using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private List<ClusterJoiningStatus> joiningRecovery = [];
    private bool joiningRecoveryBusy;
    private Guid? joiningRecoveryJob;
    private Guid? joiningRecoveryIntent;

    private async Task ReadJoiningRecoveryAsync()
    {
        if (client is null || designPreview) return;
        try
        {
            var records = await client.CallAsync<List<ClusterJoiningStatus>>("cluster-joining-read", null, TimeSpan.FromSeconds(8), lifetime.Token);
            if (records is null || records.Any(r => r is null || r.OperationId == Guid.Empty || r.ClusterId == Guid.Empty
                || r.ServerIds is null || r.ServerIds.Count == 0 || r.ServerIds.Any(id => id == Guid.Empty))) throw new InvalidDataException();
            joiningRecovery = records;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error))
        { lifecycleNotice = "Recorded cluster joining could not be checked. Review activity before changing membership."; }
    }

    private async Task FinishJoiningAsync(Guid serverId)
    {
        if (client is null || designPreview || joiningRecoveryBusy) return;
        var record = joiningRecovery.SingleOrDefault(r => r.ServerIds.Contains(serverId));
        if (record is null) return;
        if (joiningRecoveryIntent is not null && joiningRecoveryIntent != record.OperationId)
        { lifecycleNotice = "Check the earlier joining recovery request before starting another."; Render(); return; }
        joiningRecoveryBusy = true;
        try
        {
            BackgroundOperation? job;
            if (joiningRecoveryJob is Guid original)
                job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(original), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
            else
            {
                joiningRecoveryIntent = record.OperationId; joiningRecoveryJob = Guid.NewGuid();
                job = await client.CallAsync<BackgroundOperation>("cluster-joining-resume-start", record.OperationId, TimeSpan.FromSeconds(8), lifetime.Token, joiningRecoveryJob);
            }
            while (job is not null)
            {
                if (job.Id != joiningRecoveryJob || job.Kind != "cluster-joining-recovery" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<ClusterJoiningRecoveryResult>();
                    if (result?.OperationId != record.OperationId) throw new InvalidDataException();
                    lifecycleNotice = result.Completed ? "Cluster joining and inherited settings completed." : "No pending joining record was found. Review current membership and settings.";
                    joiningRecoveryJob = joiningRecoveryIntent = null; await RefreshAsync(); return;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { lifecycleNotice = job.Error ?? "Joining recovery needs review. Stop the affected servers and cluster before retrying."; joiningRecoveryJob = joiningRecoveryIntent = null; return; }
                lifecycleNotice = job.Progress?.Message ?? "Finishing recorded cluster joining…"; Render();
                await Task.Delay(500, lifetime.Token);
                job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(joiningRecoveryJob!.Value), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
            }
            await ReadJoiningRecoveryAsync();
            joiningRecoveryJob = joiningRecoveryIntent = null;
            lifecycleNotice = "The recovery job result is unavailable. Review the recorded joining state; the request was not resent.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error))
        { lifecycleNotice = "Joining recovery result is unknown. Check the existing request before trying again."; }
        finally { joiningRecoveryBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
