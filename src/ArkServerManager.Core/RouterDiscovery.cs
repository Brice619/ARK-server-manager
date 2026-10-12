using System.Net;
using System.Net.NetworkInformation;
using Mono.Nat;

namespace ArkServerManager.Core;
public sealed record RouterDiscoveryResult(bool Found, string? Gateway, string? ExternalAddress, string Message);
public static class RouterDiscovery
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static async Task<INatDevice> FindAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        var completion = new TaskCompletionSource<INatDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        HashSet<IPAddress> gateways = [];
        void Found(object? sender, DeviceEventArgs args)
        {
            if (args.Device.NatProtocol == NatProtocol.Upnp && gateways.Contains(args.Device.DeviceEndpoint.Address))
                completion.TrySetResult(args.Device);
        }
        try
        {
            gateways = NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().GatewayAddresses).Select(item => item.Address)
                .Where(LocalNetworkAddresses.UsableIPv4).Distinct().ToHashSet();
            if (gateways.Count == 0) throw new IOException("No active IPv4 gateway was found.");
            NatUtility.DeviceFound += Found;
            foreach (var gateway in gateways) NatUtility.Search(gateway, NatProtocol.Upnp);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        finally
        {
            NatUtility.DeviceFound -= Found;
            try { NatUtility.StopDiscovery(); }
            finally { Gate.Release(); }
        }
    }
    public static async Task<RouterDiscoveryResult> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            var device = await FindAsync(ct);
            var external = await device.GetExternalIPAsync().WaitAsync(TimeSpan.FromSeconds(8), ct);
            return new(true, device.DeviceEndpoint.Address.ToString(), external.ToString(),
                "A UPnP router was found. No forwarding rules were changed.");
        }
        catch (Exception error) when (error is TimeoutException or IOException or NetworkInformationException or MappingException)
        { return new(false, null, null, "Automatic router discovery could not complete. Use manual router setup."); }
    }
}
