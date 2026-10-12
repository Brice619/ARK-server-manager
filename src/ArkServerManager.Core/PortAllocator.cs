using System.Net;
using System.Net.Sockets;

namespace ArkServerManager.Core;

public sealed class PortReservation : IDisposable
{
    private readonly List<Socket> sockets;
    public int GamePort { get; }
    public int QueryPort { get; }
    public int RconPort { get; }
    internal PortReservation(int gamePort, int queryPort, int rconPort, List<Socket> sockets)
    { GamePort = gamePort; QueryPort = queryPort; RconPort = rconPort; this.sockets = sockets; }
    public void Dispose() { foreach (var socket in sockets) socket.Dispose(); }
}

/// <summary>Probes OS availability and keeps sockets reserved until the caller is ready to launch.</summary>
public sealed class PortAllocator
{
    /// <summary>Rechecks reviewed assignments without silently changing configuration ports.</summary>
    public PortReservation ReserveExact(ServerSettings requested, IEnumerable<ServerSettings> assigned)
    {
        requested.Validate();
        var ports = new[] { requested.GamePort, requested.GamePort + 1, requested.QueryPort, requested.RconPort };
        var used = assigned.SelectMany(s => new[] { s.GamePort, s.GamePort + 1, s.QueryPort, s.RconPort }).ToHashSet();
        if (ports.Any(used.Contains)) throw new IOException("Reviewed ports are now assigned to another server. Check ports again.");
        var held = new List<Socket>();
        try
        {
            foreach (var port in ports)
            {
                var type = port == requested.RconPort ? SocketType.Stream : SocketType.Dgram;
                var socket = new Socket(AddressFamily.InterNetwork, type,
                    type == SocketType.Dgram ? ProtocolType.Udp : ProtocolType.Tcp) { ExclusiveAddressUse = true };
                held.Add(socket);
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                // An exclusive bound socket reserves the port; a probe must not accept network connections.
            }
            return new(requested.GamePort, requested.QueryPort, requested.RconPort, held);
        }
        catch (SocketException error)
        {
            foreach (var socket in held) socket.Dispose();
            throw new IOException("Reviewed ports are now in use. Check ports again.", error);
        }
        catch { foreach (var socket in held) socket.Dispose(); throw; }
    }

    public PortReservation Reserve(IEnumerable<ServerSettings> assigned,
        int gameStart = 7777, int queryStart = 27015, int rconStart = 32330)
    {
        var used = assigned.SelectMany(s => new[] { s.GamePort, s.GamePort + 1, s.QueryPort, s.RconPort }).ToHashSet();
        var held = new List<Socket>();
        try
        {
            var game = Find(gameStart, 2, SocketType.Dgram, used, held);
            var query = Find(queryStart, 1, SocketType.Dgram, used, held);
            var rcon = Find(rconStart, 1, SocketType.Stream, used, held);
            return new(game, query, rcon, held);
        }
        catch { foreach (var socket in held) socket.Dispose(); throw; }
    }

    private static int Find(int start, int count, SocketType type, HashSet<int> used, List<Socket> held)
    {
        if (start < 1 || start > 65536 - count) throw new ArgumentOutOfRangeException(nameof(start));
        for (var port = start; port <= 65536 - count; port++)
        {
            if (Enumerable.Range(port, count).Any(used.Contains)) continue;
            var candidate = new List<Socket>();
            try
            {
                for (var offset = 0; offset < count; offset++)
                {
                    var socket = new Socket(AddressFamily.InterNetwork, type,
                        type == SocketType.Dgram ? ProtocolType.Udp : ProtocolType.Tcp) { ExclusiveAddressUse = true };
                    candidate.Add(socket);
                    socket.Bind(new IPEndPoint(IPAddress.Any, port + offset));
                    // Reserve only. Listening would expose the manager during a read-only setup check.
                }
                held.AddRange(candidate);
                foreach (var number in Enumerable.Range(port, count)) used.Add(number);
                return port;
            }
            catch (SocketException)
            {
                foreach (var socket in candidate) socket.Dispose();
            }
        }
        throw new IOException("No available port was found in the requested range.");
    }
}
