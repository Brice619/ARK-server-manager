using System.IO.Pipes;
using System.Runtime.Versioning;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class WindowsSnapshotPipeTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ChannelBindsBothEndsToTheirActualOwningProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = WindowsSnapshotPipe.NewName();
        await using var server = WindowsSnapshotPipe.CreateServer(name);
        var waiting = server.WaitForConnectionAsync(deadline.Token);
        await using var client = await WindowsSnapshotPipe.ConnectAsync(name, Environment.ProcessId, deadline.Token);
        await waiting;
        WindowsSnapshotPipe.RequireClient(server, Environment.ProcessId);
        Assert.Throws<IOException>(() => WindowsSnapshotPipe.RequireClient(server, Environment.ProcessId + 1));
        Assert.Throws<IOException>(() => WindowsSnapshotPipe.RequireServer(client, Environment.ProcessId + 1));
        await client.WriteAsync(new byte[] { 42 }, deadline.Token);
        var buffer = new byte[1];
        Assert.Equal(1, await server.ReadAsync(buffer, deadline.Token));
        Assert.Equal(42, buffer[0]);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ConnectionRefusesAnImpersonatedManagerAndClosesItsEndpoint()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = WindowsSnapshotPipe.NewName();
        await using var server = WindowsSnapshotPipe.CreateServer(name);
        var waiting = server.WaitForConnectionAsync(deadline.Token);
        await Assert.ThrowsAsync<IOException>(() => WindowsSnapshotPipe.ConnectAsync(name, Environment.ProcessId + 1, deadline.Token));
        await waiting;
        Assert.Equal(0, await server.ReadAsync(new byte[1], deadline.Token));
        Assert.Throws<ArgumentException>(() => WindowsSnapshotPipe.CreateServer("arbitrary-channel"));
        Assert.Throws<ArgumentException>(() => WindowsSnapshotPipe.CreateServer("ark-snapshot-" + Guid.Empty.ToString("N")));
    }
}
