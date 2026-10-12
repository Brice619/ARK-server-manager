using System.Net;
using System.Net.Sockets;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class PortTests
{
    [Fact]
    public void AvoidsAssignedAndOccupiedPortsAndReleasesReservations()
    {
        using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        occupied.Bind(new IPEndPoint(IPAddress.Any, 0));
        var first = ((IPEndPoint)occupied.LocalEndPoint!).Port;
        var allocator = new PortAllocator();
        using var reservation = allocator.Reserve([], gameStart: first, queryStart: 27015, rconStart: 32330);
        Assert.NotEqual(first, reservation.GamePort);
        using var next = allocator.Reserve([new ServerSettings { GamePort = reservation.GamePort, QueryPort = reservation.QueryPort, RconPort = reservation.RconPort }],
            gameStart: reservation.GamePort, queryStart: reservation.QueryPort, rconStart: reservation.RconPort);
        Assert.NotEqual(reservation.GamePort, next.GamePort);
        Assert.NotEqual(reservation.QueryPort, next.QueryPort);
        Assert.NotEqual(reservation.RconPort, next.RconPort);
        var port = reservation.RconPort;
        reservation.Dispose();
        using var released = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        released.Bind(new IPEndPoint(IPAddress.Any, port));
    }
}
