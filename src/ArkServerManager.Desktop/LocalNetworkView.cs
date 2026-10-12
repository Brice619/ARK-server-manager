using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Net;

namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private LocalNetworkReport? localNetworkReport;
    private bool networkAddressesBusy;
    private string networkAddressNotice = "Refresh to see this computer’s active IPv4 addresses.";
    private void AddLocalNetworkConnections(StackPanel body, ServerDefinition server)
    {
        var details = new StackPanel { Spacing = 12 };
        details.Children.Add(Label(networkAddressNotice, 15));
        details.Children.Add(Label("Use an address on the same network as your players. VPN and virtual adapters may appear too. An address alone does not verify that players can connect.", 15));
        if (localNetworkReport is { } report)
        {
            foreach (var address in report.Addresses)
                ReadOnlyField(details, address.Interface, address.Address + ":" + server.Settings.GamePort);
            foreach (var finding in report.Findings) details.Children.Add(Label(finding, 14));
        }
        details.Children.Add(ActionButton(networkAddressesBusy ? "Refreshing…" : "Refresh addresses", client is null || networkAddressesBusy ? null : () => _ = RefreshNetworkAddressesAsync()));
        if (OperatingSystem.IsWindows()) details.Children.Add(ActionButton(pendingFirewall is null ? "Set up Windows Firewall…" : "Check firewall setup",
            client is null || firewallBusy ? null : () => _ = SetupFirewallAsync(server)));
        body.Children.Add(new Expander { Header = "Local network connections", Content = details, Background = Brush.Parse("#101C26") });
        var internet = new StackPanel { Spacing = 12 };
        internet.Children.Add(Label("Forward player ports to this computer", 21, true));
        internet.Children.Add(ActionButton(pendingRouter is null ? "Automatic router setup…" : "Check router setup",
            client is null || routerBusy || pendingRouterRemoval is not null ? null : () => _ = SetupRouterAsync(server)));
        internet.Children.Add(ActionButton(pendingRouterRemoval is null ? "Remove app router rules…" : "Check router removal",
            client is null || routerBusy || pendingRouter is not null ? null : () => _ = RemoveRouterRulesAsync(server)));
        internet.Children.Add(Label("In your router’s port-forwarding settings, choose this computer’s LAN IPv4 address and forward these UDP ports using the same external and internal numbers:", 16));
        ReadOnlyField(internet, "Game ports · UDP", server.Settings.GamePort + ", " + (server.Settings.GamePort + 1));
        ReadOnlyField(internet, "Query port · UDP", server.Settings.QueryPort.ToString());
        internet.Children.Add(Label("Reserve this computer’s LAN address in the router so it stays the same. Players outside your network use your router’s public address or the server browser. Keep the administrator port out of player forwarding rules.", 15));
        internet.Children.Add(Label("If your router is behind another router or your internet provider uses shared addresses, ordinary forwarding may not be enough. Firewall and router settings do not prove a successful player connection.", 15));
        body.Children.Add(new Expander { Header = "Internet router setup", Content = internet, Background = Brush.Parse("#101C26") });
    }
    private async Task RefreshNetworkAddressesAsync()
    {
        if (client is null || networkAddressesBusy) return;
        networkAddressesBusy = true; Render();
        try
        {
            var report = await client.CallAsync<LocalNetworkReport>("local-network-addresses", null, TimeSpan.FromSeconds(8), lifetime.Token);
            if (report.MeasuredAt == default || report.Addresses is null || report.Addresses.Count > 64 || report.Findings is null
                || report.Addresses.Any(item => item is null || string.IsNullOrWhiteSpace(item.Interface)
                    || !IPAddress.TryParse(item.Address, out var address) || !LocalNetworkAddresses.UsableIPv4(address))) throw new InvalidDataException();
            localNetworkReport = report;
            networkAddressNotice = report.Addresses.Count == 0 ? "No active IPv4 addresses were found." : "Addresses refreshed at " + report.MeasuredAt.ToLocalTime().ToString("t") + ".";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error))
        { localNetworkReport = null; networkAddressNotice = "Network addresses could not be loaded. Try again."; }
        finally { networkAddressesBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
