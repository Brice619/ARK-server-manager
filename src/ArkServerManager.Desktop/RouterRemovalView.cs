using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;
namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private RouterRemovalSelection? pendingRouterRemoval;
    private Guid routerRemovalId;
    private async Task RemoveRouterRulesAsync(ServerDefinition server)
    {
        if (client is null || routerBusy) return;
        routerBusy = true;
        try
        {
            var reconnect = pendingRouterRemoval is not null;
            if (!reconnect)
            {
                var status = await client.CallAsync<RouterSetupStatus>("router-forwarding-record", server.Id, TimeSpan.FromSeconds(8), lifetime.Token);
                if (status.ServerId != server.Id) throw new InvalidDataException();
                if (status.Record is null || status.Record.Phase == RouterSetupPhase.Removed)
                { networkAddressNotice = "No active app router rules are recorded for this server."; return; }
                var recorded = status.Record;
                if (recorded.ServerId != server.Id || recorded.OperationId == Guid.Empty || !Enum.IsDefined(recorded.Phase)) throw new InvalidDataException();
                var dialog = new Window { Title = "Remove app router rules?", Width = 580, Height = 350,
                    Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var body = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
                body.Children.Add(Label("Remove internet forwarding?", 25, true));
                body.Children.Add(Label("This removes the app’s recorded player UDP rules on " + recorded.Gateway + ". Internet players may lose access. Rules belonging to other applications are kept.", 16));
                body.Children.Add(ActionButton("Remove app rules", () => dialog.Close(true), true));
                body.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = body;
                if (!await dialog.ShowDialog<bool>(this)) return;
                pendingRouterRemoval = new(server.Id, recorded.OperationId, true); routerRemovalId = Guid.NewGuid();
            }
            var job = reconnect
                ? (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(routerRemovalId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation
                : await client.CallAsync<BackgroundOperation>("router-forwarding-remove-start", pendingRouterRemoval, TimeSpan.FromSeconds(8), lifetime.Token, routerRemovalId);
            while (true)
            {
                if (job is null)
                {
                    var current = await client.CallAsync<RouterSetupStatus>("router-forwarding-record", pendingRouterRemoval!.ServerId, TimeSpan.FromSeconds(8), lifetime.Token);
                    if (current.ServerId != pendingRouterRemoval.ServerId || current.Record is { } recorded && (recorded.ServerId != current.ServerId
                        || recorded.SchemaVersion != 1 || !Enum.IsDefined(recorded.Phase))) throw new InvalidDataException();
                    networkAddressNotice = current.Record switch
                    {
                        { OperationId: var operation } when operation != pendingRouterRemoval.ExpectedOperationId => "A different router setup is recorded. Review it before removing any rules.",
                        { Phase: RouterSetupPhase.Removed } => "The original router setup is recorded as removed. The original operation result is unavailable.",
                        null => "The router ownership record is unavailable. Inspect the router before making another change.",
                        _ => "Router removal has no retained result. Recorded rules may still exist; review them before confirming another removal."
                    };
                    pendingRouterRemoval = null;
                    break;
                }
                if (job.Id != routerRemovalId || job.Kind != "router-forwarding-remove" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<RouterSetupStatus>();
                    if (result?.Record is not { Phase: RouterSetupPhase.Removed } record || result.ServerId != pendingRouterRemoval!.ServerId
                        || record.OperationId != pendingRouterRemoval.ExpectedOperationId) throw new InvalidDataException();
                    pendingRouterRemoval = null; networkAddressNotice = "App router rules removed."; break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { pendingRouterRemoval = null; networkAddressNotice = job.Error ?? "Some router rules need review."; break; }
                networkAddressNotice = job.Progress?.Message ?? "Router removal is queued."; Render();
                await Task.Delay(500, lifetime.Token);
                job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(routerRemovalId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error))
        { networkAddressNotice = pendingRouterRemoval is null ? "Router rules could not be reviewed." : "Removal outcome unknown. Reopen removal to check the original request."; }
        finally { routerBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
