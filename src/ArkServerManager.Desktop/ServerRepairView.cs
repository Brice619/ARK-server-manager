using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;
namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private ServerRepairSelection? pendingRepair;
    private Guid repairRequestId;
    private bool repairBusy;
    private async Task RepairServerFilesAsync(ServerDefinition server)
    {
        if (client is null || repairBusy) return;
        repairBusy = true;
        try
        {
            var reconnect = pendingRepair is not null;
            if (!reconnect)
            {
                var dialog = new Window { Title = "Check and repair server files?", Width = 590, Height = 360,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
                var body = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
                body.Children.Add(Label(server.Settings.Name, 25, true));
                body.Children.Add(Label("Only this server will stop. SteamCMD checks its package files and downloads missing or damaged files. It may update the server to the current build. Other cluster maps keep running.", 16));
                var start = new CheckBox { Content = "Start this server after repair", IsChecked = false }; body.Children.Add(start);
                body.Children.Add(ActionButton("Stop and check files", () => dialog.Close(true), true));
                body.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = body;
                if (!await dialog.ShowDialog<bool>(this)) return;
                pendingRepair = new(server.Id, server.Directory, start.IsChecked == true, true); repairRequestId = Guid.NewGuid();
            }
            lifecycleNotice = "File validation is queued."; Render();
            var job = reconnect
                ? (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(repairRequestId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation
                : await client.CallAsync<BackgroundOperation>("server-files-repair-start", pendingRepair, TimeSpan.FromSeconds(8), lifetime.Token, repairRequestId);
            while (true)
            {
                if (job is null)
                {
                    var status = await client.CallAsync<ServerRepairStatus>("server-files-repair-record", pendingRepair!.ServerId, TimeSpan.FromSeconds(8), lifetime.Token);
                    if (status.ServerId != pendingRepair.ServerId || status.Record is { } record && (record.ServerId != pendingRepair.ServerId
                        || record.Directory != pendingRepair.Directory || record.SchemaVersion != 1 || !Enum.IsDefined(record.Phase))) throw new InvalidDataException();
                    lifecycleNotice = status.Record switch
                    {
                        { OperationId: var operation } when operation != repairRequestId => "A different repair is recorded. Review current server state before another repair.",
                        { Phase: ServerRepairPhase.Completed } => "The original repair is recorded as complete. Check the server's current running state.",
                        { Phase: ServerRepairPhase.Validated or ServerRepairPhase.Starting } => "File validation completed, but startup completion is unknown. Review current server state before starting it.",
                        null => "No repair record is available. Review running installation tools and server state before another repair.",
                        _ => "File repair was interrupted or did not finish. Review running installation tools before confirming another repair."
                    };
                    await RefreshAsync(); pendingRepair = null; return;
                }
                if (job.Id != repairRequestId || job.Kind != "server-files-repair" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<ServerRepairResult>();
                    if (result is null || result.ServerId != pendingRepair!.ServerId || !result.FilesValidated || result.Started != pendingRepair.StartAfterRepair)
                        throw new InvalidDataException();
                    pendingRepair = null; lifecycleNotice = result.Started ? "Server files validated and the selected server started." : "Server files validated. The selected server remains stopped.";
                    await RefreshAsync(); return;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { pendingRepair = null; lifecycleNotice = job.Error ?? "File validation did not complete. Review the server before starting it."; return; }
                lifecycleNotice = job.Progress?.Message ?? "File validation is queued."; Render();
                await Task.Delay(500, lifetime.Token);
                job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(repairRequestId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error))
        { lifecycleNotice = pendingRepair is null ? "File repair could not be reviewed." : "File-repair outcome unknown. Check the original result before another repair."; }
        finally { repairBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
