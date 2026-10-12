using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ClusterRunningTimeTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public void Advance(int minutes) => Ticks += TimeSpan.FromMinutes(minutes).Ticks;
    }

    [Fact]
    public void CountsOverlappingMembersOnceAndContinuesWhenOneFails()
    {
        var clock = new Clock(); var tracker = new ClusterRunningTimeTracker(clock);
        var cluster = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        tracker.Observe(first, cluster, ServerState.Running); clock.Advance(20);
        tracker.Observe(second, cluster, ServerState.Running); clock.Advance(20);
        tracker.Observe(first, cluster, ServerState.Recovering); clock.Advance(20);
        Assert.Equal(TimeSpan.FromHours(1), tracker.Runtime(cluster));
        tracker.Observe(second, cluster, ServerState.Stopping); clock.Advance(2);
        tracker.Observe(second, cluster, ServerState.Stopped); clock.Advance(120);
        Assert.Equal(TimeSpan.FromMinutes(62), tracker.Runtime(cluster));
        tracker.Observe(first, cluster, ServerState.Running); clock.Advance(10);
        Assert.Equal(TimeSpan.FromMinutes(72), tracker.Runtime(cluster));
    }

    [Fact]
    public void CountsNonOverlappingRunsAndKeepsClustersIndependent()
    {
        var clock = new Clock(); var tracker = new ClusterRunningTimeTracker(clock);
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        tracker.Observe(first, a, ServerState.Running); clock.Advance(30);
        tracker.Observe(first, a, ServerState.Stopped); clock.Advance(180);
        tracker.Observe(second, a, ServerState.Running); clock.Advance(30);
        Assert.Equal(TimeSpan.FromHours(1), tracker.Runtime(a));
        tracker.Observe(second, b, ServerState.Running); clock.Advance(15);
        Assert.Equal(TimeSpan.FromHours(1), tracker.Runtime(a));
        Assert.Equal(TimeSpan.FromMinutes(15), tracker.Runtime(b));
        tracker.Observe(second, null, ServerState.Running); clock.Advance(60);
        Assert.Equal(TimeSpan.FromMinutes(15), tracker.Runtime(b));
    }
}
