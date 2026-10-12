using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ArkGracefulShutdownTests
{
    private static RconEndpoint Endpoint => new(32330, "fixture", TimeSpan.FromSeconds(1), RconResponseMode.SinglePacket);

    [Fact]
    public async Task RequestsExitOnlyAfterConfirmedSave()
    {
        var calls = new List<string>();
        var shutdown = new ArkGracefulShutdown((_, command, _) => { calls.Add(command); return Task.FromResult(""); });
        await shutdown.RequestAsync(() => true, Endpoint, _ => { calls.Add("confirmed"); return Task.CompletedTask; });
        Assert.Equal(new[] { "SaveWorld", "confirmed", "DoExit" }, calls);
    }

    [Fact]
    public async Task FailedSaveConfirmationNeverRequestsExit()
    {
        var calls = new List<string>();
        var shutdown = new ArkGracefulShutdown((_, command, _) => { calls.Add(command); return Task.FromResult(""); });
        await Assert.ThrowsAsync<IOException>(() => shutdown.RequestAsync(() => true, Endpoint,
            _ => throw new IOException("Save not confirmed")));
        Assert.Equal(new[] { "SaveWorld" }, calls);
    }

    [Fact]
    public async Task CancellationAfterSaveNeverRequestsExit()
    {
        var calls = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var shutdown = new ArkGracefulShutdown((_, command, _) => { calls.Add(command); return Task.FromResult(""); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shutdown.RequestAsync(() => true, Endpoint,
            _ => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
        Assert.Equal(new[] { "SaveWorld" }, calls);
    }
}
