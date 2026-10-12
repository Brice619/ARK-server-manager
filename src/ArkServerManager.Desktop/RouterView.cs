using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;
namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private RouterForwardingSelection? pendingRouter;
    private Guid routerRequestId;
    private bool routerBusy;
    private async Task SetupRouterAsync(ServerDefinition server)
    {
        if (client is null || routerBusy) return;
        routerBusy = true;
        try
        {
            var reconnect = pendingRouter is not null;
            if (!reconnect)
            {
                var status = await client.CallAsync<RouterSetupStatus>("router-forwarding-record", server.Id, TimeSpan.FromSeconds(8), lifetime.Token);
                if (status.ServerId != server.Id) throw new InvalidDataException();
                if (status.Record is { Phase: not RouterSetupPhase.Removed } recorded)
                {
                    if (recorded.ServerId != server.Id || recorded.SchemaVersion != 1 || !Enum.IsDefined(recorded.Phase)) throw new InvalidDataException();
                    networkAddressNotice = recorded.Phase == RouterSetupPhase.Completed
                        ? "Router setup is recorded. UDP ports " + recorded.GamePort + ", " + (recorded.GamePort + 1) + ", " + recorded.QueryPort + " were forwarded through " + recorded.Gateway + "."
                        : "An interrupted router setup needs review. Its rules may still exist; use your router’s settings to inspect them.";
                    return;
                }
                var dialog = new Window { Title = "Set up router forwarding?", Width = 620, Height = 420,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
                panel.Children.Add(Label("Allow internet player connections", 25, true));
                panel.Children.Add(Label(server.Settings.Name + " · UDP " + server.Settings.GamePort + ", " + (server.Settings.GamePort + 1) + ", " + server.Settings.QueryPort, 17));
                panel.Children.Add(Label("The app will look for UPnP support on your active router and forward these player ports to this computer. Existing forwarding rules are kept. The administrator port is excluded. Rules remain until removed or cleared by your router.", 16));
                panel.Children.Add(Label("If automatic setup is unavailable, follow the manual instructions. Forwarding alone does not prove players can connect.", 15));
                panel.Children.Add(ActionButton("Set up forwarding", () => dialog.Close(true), true));
                panel.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = new ScrollViewer { Content = panel };
                if (!await dialog.ShowDialog<bool>(this)) return;
                pendingRouter = new(server.Id, server.Directory, server.Settings.GamePort, server.Settings.QueryPort, true); routerRequestId = Guid.NewGuid();
            }
            Render();
            var job = reconnect
                ? (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(routerRequestId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation
                : await client.CallAsync<BackgroundOperation>("router-forwarding-setup-start", pendingRouter, TimeSpan.FromSeconds(8), lifetime.Token, routerRequestId);
            while (true)
            {
                if (job is null)
                {
                    var recorded = await client.CallAsync<RouterSetupStatus>("router-forwarding-record", pendingRouter!.ServerId, TimeSpan.FromSeconds(8), lifetime.Token);
                    if (recorded.ServerId != pendingRouter.ServerId || recorded.Record is { } record && (record.ServerId != pendingRouter.ServerId
                        || record.Directory != pendingRouter.Directory || record.SchemaVersion != 1 || !Enum.IsDefined(record.Phase)))
                        throw new InvalidDataException();
                    networkAddressNotice = recorded.Record switch
                    {
                        { OperationId: var operation } when operation != routerRequestId => "A different router setup is recorded. Review the current forwarding before making another change.",
                        { Phase: RouterSetupPhase.Completed } => "The original router setup is recorded as complete. Player reachability still needs checking.",
                        { Phase: RouterSetupPhase.Removed } => "The router setup is recorded as removed. The original operation result is unavailable.",
                        null => "No router setup record is present. The original result is unavailable; review the router before starting another setup.",
                        _ => "An interrupted router setup is recorded. Its rules may still exist; inspect them or use Remove router forwarding to clean up the recorded rules."
                    };
                    pendingRouter = null;
                    break;
                }
                if (job.Id != routerRequestId || job.Kind != "router-forwarding-setup" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<RouterForwardingResult>();
                    if (result is null || result.ServerId != pendingRouter!.ServerId || result.Ports is null
                        || !result.Ports.SequenceEqual(new[] { pendingRouter.GamePort, pendingRouter.GamePort + 1, pendingRouter.QueryPort })) throw new InvalidDataException();
                    pendingRouter = null; networkAddressNotice = "Router player ports forwarded. Firewall setup and a player connection still need checking."; break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { pendingRouter = null; networkAddressNotice = job.Error ?? "Router setup did not complete. Use manual setup or review existing rules."; break; }
                networkAddressNotice = job.Progress?.Message ?? "Router setup is queued."; Render();
                await Task.Delay(500, lifetime.Token);
                job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(routerRequestId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error))
        { networkAddressNotice = pendingRouter is null ? "Router setup could not be reviewed. Manual setup is available." : "Router outcome unknown. Check the original setup request before another change."; }
        finally { routerBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
