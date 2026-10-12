using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ArkServerManager.Core;
public sealed record LocalNetworkAddress(string Interface, string Address);
public sealed record LocalNetworkReport(DateTimeOffset MeasuredAt, IReadOnlyList<LocalNetworkAddress> Addresses, IReadOnlyList<string> Findings);
public static class LocalNetworkAddresses
{
    public static LocalNetworkReport Read()
    {
        var addresses = new List<LocalNetworkAddress>();
        var findings = new List<string>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                foreach (var address in adapter.GetIPProperties().UnicastAddresses.Select(item => item.Address))
                    if (UsableIPv4(address)) addresses.Add(new(adapter.Name, address.ToString()));
            }
            catch (NetworkInformationException) { findings.Add("An active network adapter could not be inspected."); }
        }
        return new(DateTimeOffset.UtcNow, addresses.Distinct().OrderBy(item => item.Interface).ThenBy(item => item.Address).Take(64).ToArray(), findings);
    }

    public static bool UsableIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254)
            && !address.Equals(IPAddress.Broadcast);
    }
}
