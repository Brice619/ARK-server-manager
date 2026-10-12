using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerRconReadinessTests
{
    private static RconEndpoint Endpoint => new(32330, "secret", TimeSpan.FromSeconds(1), RconResponseMode.SinglePacket);

    [Fact]
    public async Task RetriesTransientConnectionUntilAuthenticatedResponse()
    {
        var attempts = 0;
        var readiness = new ServerRconReadiness((endpoint, command, _) =>
        {
            Assert.Equal("ListPlayers", command);
            if (++attempts < 3) throw new TimeoutException();
            return Task.FromResult("No Players Connected");
        });
        await readiness.WaitAsync(() => true, Endpoint, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task WrongPasswordFailsWithoutRetrying()
    {
        var attempts = 0;
        var readiness = new ServerRconReadiness((_, _, _) => { attempts++; throw new UnauthorizedAccessException(); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => readiness.WaitAsync(() => true, Endpoint,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ProcessExitDuringResponseNeverReportsReady()
    {
        var alive = true;
        var readiness = new ServerRconReadiness((_, _, _) => { alive = false; return Task.FromResult(""); });
        await Assert.ThrowsAsync<IOException>(() => readiness.WaitAsync(() => alive, Endpoint,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public async Task StartupDeadlineBoundsUnresponsiveConnection()
    {
        var readiness = new ServerRconReadiness(async (_, _, ct) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return ""; });
        await Assert.ThrowsAsync<TimeoutException>(() => readiness.WaitAsync(() => true, Endpoint,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(1)));
    }
}
