using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ArkServerManager.Core;

public enum RconResponseMode { SinglePacket, Sentinel }
public sealed record RconEndpoint(int Port, string Password, TimeSpan Timeout, RconResponseMode ResponseMode)
{
    public override string ToString() => $"RconEndpoint {{ Port = {Port}, Password = [redacted], Timeout = {Timeout}, ResponseMode = {ResponseMode} }}";
}
public sealed record RconPacket(int Id, int Type, byte[] Body);

public static class RconProtocol
{
    public const int MaximumPacketSize = 4096;

    public static byte[] Encode(int id, int type, string body)
    {
        if (body.Contains('\0')) throw new ArgumentException("RCON values cannot contain a null character.");
        var bytes = Encoding.UTF8.GetBytes(body);
        var size = bytes.Length + 10;
        if (size > MaximumPacketSize) throw new ArgumentException("The RCON request is too long.");
        var packet = new byte[size + 4];
        BinaryPrimitives.WriteInt32LittleEndian(packet, size);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        bytes.CopyTo(packet, 12);
        return packet;
    }

    public static async Task<RconPacket> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        var size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size is < 10 or > MaximumPacketSize) throw new InvalidDataException("Invalid RCON packet length.");
        var data = new byte[size];
        await stream.ReadExactlyAsync(data, cancellationToken);
        if (data[^1] != 0 || data[^2] != 0) throw new InvalidDataException("Invalid RCON packet terminator.");
        return new(BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)), data[8..^2]);
    }
}

/// <summary>One authenticated local connection per command avoids stale responses and credential logging.</summary>
public sealed class RconClient
{
    public async Task<string> ExecuteAsync(RconEndpoint endpoint, string command, CancellationToken cancellationToken = default)
    {
        if (endpoint.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(endpoint.Port));
        if (endpoint.Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(endpoint.Timeout));
        if (string.IsNullOrWhiteSpace(endpoint.Password)) throw new ArgumentException("An administrator password is required.");
        if (string.IsNullOrWhiteSpace(command) || command.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("Enter a single administrator command.");
        var auth = RconProtocol.Encode(1, 3, endpoint.Password);
        var request = RconProtocol.Encode(2, 2, command);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(endpoint.Timeout);
        var ct = deadline.Token;
        using var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, ct);
            await using var stream = client.GetStream();
            await stream.WriteAsync(auth, ct);
            var authenticated = false;
            for (var i = 0; i < 4; i++)
            {
                var reply = await RconProtocol.ReadAsync(stream, ct);
                if (reply.Id == -1) throw new UnauthorizedAccessException("The server rejected the administrator password.");
                if (reply.Id != 1) throw new InvalidDataException("Unexpected authentication response ID.");
                if (reply.Type == 2) { authenticated = true; break; }
                if (reply.Type != 0 || reply.Body.Length != 0) throw new InvalidDataException("Invalid authentication response.");
            }
            if (!authenticated) throw new InvalidDataException("Missing authentication response.");
            await stream.WriteAsync(request, ct);
            if (endpoint.ResponseMode == RconResponseMode.Sentinel)
                await stream.WriteAsync(RconProtocol.Encode(3, 0, ""), ct);
            using var body = new MemoryStream();
            for (var i = 0; i < 1024; i++)
            {
                var reply = await RconProtocol.ReadAsync(stream, ct);
                if (reply.Type != 0) throw new InvalidDataException("Unexpected command response type.");
                if (reply.Id == 3 && endpoint.ResponseMode == RconResponseMode.Sentinel)
                    return new UTF8Encoding(false, true).GetString(body.ToArray());
                if (reply.Id != 2) throw new InvalidDataException("Unexpected command response ID.");
                await body.WriteAsync(reply.Body, ct);
                if (endpoint.ResponseMode == RconResponseMode.SinglePacket)
                    return new UTF8Encoding(false, true).GetString(body.ToArray());
            }
            throw new InvalidDataException("The server returned too many response packets.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The local administrator connection timed out.");
        }
        finally { Array.Clear(auth); }
    }
}
