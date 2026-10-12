using System.Net;
using System.Net.Sockets;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ExactPortReservationTests
{
    [Fact]
    public async Task PortChecksReserveAdministrationPortWithoutAcceptingConnections()
    {
        var allocator = new PortAllocator();
        using (var discovered = allocator.Reserve([], 41000, 42000, 43000))
        {
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await Assert.ThrowsAsync<SocketException>(async () => await client.ConnectAsync(IPAddress.Loopback, discovered.RconPort));
        }
        using var exact = allocator.ReserveExact(Available(), []);
        using var exactClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsAsync<SocketException>(async () => await exactClient.ConnectAsync(IPAddress.Loopback, exact.RconPort));
    }

    private static ServerSettings Available()
    {
        using var probe = new PortAllocator().Reserve([], 41000, 42000, 43000);
        return new() { Name = "Fixture", GamePort = probe.GamePort, QueryPort = probe.QueryPort, RconPort = probe.RconPort };
    }

    [Fact]
    public void HoldsReviewedPortsUntilDisposed()
    {
        var settings = Available(); var allocator = new PortAllocator();
        using (var held = allocator.ReserveExact(settings, []))
        {
            Assert.Equal(settings.GamePort, held.GamePort);
            Assert.Equal(settings.QueryPort, held.QueryPort);
            Assert.Equal(settings.RconPort, held.RconPort);
            Assert.Throws<IOException>(() => allocator.ReserveExact(settings, []));
        }
        using var reacquired = allocator.ReserveExact(settings, []);
    }

    [Fact]
    public void FailedLateBindReleasesEarlierPortsAndDoesNotSubstitute()
    {
        var settings = Available();
        using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        occupied.Bind(new IPEndPoint(IPAddress.Any, settings.RconPort)); occupied.Listen(1);
        var allocator = new PortAllocator();
        Assert.Throws<IOException>(() => allocator.ReserveExact(settings, []));
        foreach (var port in new[] { settings.GamePort, settings.GamePort + 1, settings.QueryPort })
        {
            using var freed = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            freed.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        occupied.Dispose();
        using var reserved = allocator.ReserveExact(settings, []);
    }

    [Fact]
    public void RefusesCatalogAssignmentEvenWhenOperatingSystemPortsAreFree()
    {
        var settings = Available();
        Assert.Throws<IOException>(() => new PortAllocator().ReserveExact(settings, [settings]));
        using var stillFree = new PortAllocator().ReserveExact(settings, []);
    }
}
