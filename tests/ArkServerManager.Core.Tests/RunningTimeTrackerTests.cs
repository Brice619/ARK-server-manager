using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class RunningTimeTrackerTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public void Advance(TimeSpan elapsed) => Ticks += elapsed.Ticks;
    }
    [Fact]
    public void CountsRunningAndGracefulStoppingButNotStartupRecoveryOrStoppedTime()
    {
        var clock = new Clock(); var tracker = new RunningTimeTracker(clock);
        clock.Advance(TimeSpan.FromHours(5)); tracker.Transition(ServerState.Starting);
        clock.Advance(TimeSpan.FromMinutes(20)); tracker.Transition(ServerState.Running);
        clock.Advance(TimeSpan.FromMinutes(40)); tracker.Transition(ServerState.Stopping);
        clock.Advance(TimeSpan.FromMinutes(2)); tracker.Transition(ServerState.Stopped);
        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(TimeSpan.FromMinutes(42), tracker.Runtime);
        tracker.Transition(ServerState.Recovering); clock.Advance(TimeSpan.FromMinutes(10));
        tracker.Transition(ServerState.Running); clock.Advance(TimeSpan.FromMinutes(18));
        Assert.Equal(TimeSpan.FromHours(1), tracker.Runtime);
        tracker.Transition(ServerState.Failed); clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(TimeSpan.FromHours(1), tracker.Runtime);
    }
    [Fact]
    public void RepeatedReadsAndSameStateTransitionsDoNotDoubleCount()
    {
        var clock = new Clock(); var tracker = new RunningTimeTracker(clock);
        tracker.Transition(ServerState.Running); clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), tracker.Runtime);
        Assert.Equal(TimeSpan.FromSeconds(10), tracker.Runtime);
        tracker.Transition(ServerState.Running); clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(20), tracker.Runtime);
    }
}
