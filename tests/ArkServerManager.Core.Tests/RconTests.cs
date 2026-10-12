using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class RconTests
{
    private static async Task SendFragmentedAsync(Stream stream, byte[] packet)
    {
        foreach (var value in packet) await stream.WriteAsync(new byte[] { value });
    }

    [Fact]
    public async Task AuthenticatesAndCombinesFragmentedMultipartResponses()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var fixture = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = peer.GetStream();
            var auth = await RconProtocol.ReadAsync(stream, timeout.Token);
            Assert.Equal(3, auth.Type);
            Assert.Equal("test-password", System.Text.Encoding.UTF8.GetString(auth.Body));
            await SendFragmentedAsync(stream, RconProtocol.Encode(auth.Id, 0, ""));
            await SendFragmentedAsync(stream, RconProtocol.Encode(auth.Id, 2, ""));
            var request = await RconProtocol.ReadAsync(stream, timeout.Token);
            Assert.Equal("ListPlayers", System.Text.Encoding.UTF8.GetString(request.Body));
            var sentinel = await RconProtocol.ReadAsync(stream, timeout.Token);
            Assert.Equal(0, sentinel.Type);
            await SendFragmentedAsync(stream, RconProtocol.Encode(request.Id, 0, "first "));
            await SendFragmentedAsync(stream, RconProtocol.Encode(request.Id, 0, "second"));
            await SendFragmentedAsync(stream, RconProtocol.Encode(sentinel.Id, 0, ""));
        }, timeout.Token);
        var output = await new RconClient().ExecuteAsync(new(port, "test-password", TimeSpan.FromSeconds(5), RconResponseMode.Sentinel), "ListPlayers", timeout.Token);
        Assert.Equal("first second", output);
        await fixture;
    }

    [Fact]
    public async Task WrongPasswordIsReportedWithoutExposingIt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var fixture = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = peer.GetStream();
            await RconProtocol.ReadAsync(stream, timeout.Token);
            await stream.WriteAsync(RconProtocol.Encode(-1, 2, ""), timeout.Token);
        }, timeout.Token);
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new RconClient().ExecuteAsync(
            new(port, "private-password", TimeSpan.FromSeconds(5), RconResponseMode.SinglePacket), "ListPlayers", timeout.Token));
        Assert.DoesNotContain("private-password", error.ToString());
        await fixture;
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(4097)]
    public async Task RejectsInvalidLengthsBeforeAllocatingBody(int size)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, size);
        await using var input = new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(() => RconProtocol.ReadAsync(input, default));
    }

    [Fact]
    public async Task DeadlineStopsAnUnresponsiveConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var execution = new RconClient().ExecuteAsync(new(port, "password", TimeSpan.FromMilliseconds(150), RconResponseMode.SinglePacket), "ListPlayers");
        using var peer = await listener.AcceptTcpClientAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => execution);
    }
}
