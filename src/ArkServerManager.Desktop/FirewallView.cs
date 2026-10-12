using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private FirewallSelection? pendingFirewall;
    private Guid firewallRequestId;
    private bool firewallBusy;
    private async Task SetupFirewallAsync(ServerDefinition server)
    {
        if (client is null || firewallBusy) return;
        firewallBusy = true;
        try
        {
            var reconnect = pendingFirewall is not null;
            if (!reconnect)
            {
                var dialog = new Window { Title = "Set up Windows Firewall?", Width = 620, Height = 430,
                    Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
                panel.Children.Add(Label("Allow player connections", 25, true));
                panel.Children.Add(Label(server.Settings.Name + " · UDP " + server.Settings.GamePort + ", " + (server.Settings.GamePort + 1) + ", " + server.Settings.QueryPort, 17));
                var scope = new ComboBox { ItemsSource = new[] { "Local network only", "Internet connections" }, SelectedIndex = server.NetworkScope == ServerNetworkScope.Internet ? 1 : 0 };
                panel.Children.Add(scope);
                panel.Children.Add(Label("The rule applies to this server executable. Windows will request administrator access. Internet play also requires router setup. This setup does not open the administrator port.", 16));
                panel.Children.Add(ActionButton("Apply firewall rule", () => dialog.Close(true), true));
                panel.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = new ScrollViewer { Content = panel };
                if (!await dialog.ShowDialog<bool>(this)) return;
                pendingFirewall = new(server.Id, server.Directory, server.Edition, server.Settings.GamePort, server.Settings.QueryPort,
                    scope.SelectedIndex == 1 ? ServerNetworkScope.Internet : ServerNetworkScope.LocalNetwork, true);
                firewallRequestId = Guid.NewGuid();
            }
            Render();
            BackgroundOperation? job = reconnect
                ? (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(firewallRequestId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation
                : await client.CallAsync<BackgroundOperation>("server-firewall-setup-start", pendingFirewall, TimeSpan.FromSeconds(8), lifetime.Token, firewallRequestId);
            while (true)
            {
                if (job is null)
                {
                    var receipt = await client.CallAsync<FirewallRecordedStatus>("server-firewall-recorded", pendingFirewall!.ServerId, TimeSpan.FromSeconds(8), lifetime.Token);
                    if (receipt.ServerId != pendingFirewall.ServerId) throw new InvalidDataException();
                    var record = receipt.Record;
                    if (record is { Completed: true, Removed: false } && record.OperationId == firewallRequestId
                        && record.Directory == pendingFirewall.Directory && record.Scope == pendingFirewall.Scope)
                    {
                        networkAddressNotice = "The original firewall setup recorded completion. Check the current rule and player reachability; its live operation result is unavailable.";
                        pendingFirewall = null;
                    }
                    else networkAddressNotice = "The original firewall result is unavailable and recorded completion cannot be confirmed. Review Windows Firewall before another setup; this request has not been resent.";
                    break;
                }
                if (job.Id != firewallRequestId || job.Kind != "server-firewall-setup" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<FirewallSetupResult>();
                    if (result is null || result.ServerId != pendingFirewall!.ServerId || result.Scope != pendingFirewall.Scope) throw new InvalidDataException();
                    pendingFirewall = null; networkAddressNotice = "Windows firewall rule created. Player reachability still needs checking."; break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { pendingFirewall = null; networkAddressNotice = job.Error ?? "Firewall setup did not complete."; break; }
                networkAddressNotice = job.Progress?.Message ?? "Firewall setup is queued."; Render();
                await Task.Delay(500, lifetime.Token);
                job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(firewallRequestId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error))
        { networkAddressNotice = pendingFirewall is null ? "Firewall review could not open." : "Firewall outcome unknown. Check the original setup request."; }
        finally { firewallBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
