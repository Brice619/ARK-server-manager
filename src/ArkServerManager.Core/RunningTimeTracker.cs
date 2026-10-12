namespace ArkServerManager.Core;

/// <summary>Monotonic ready-server runtime; wall-clock changes and recovery do not age backups.</summary>
public sealed class RunningTimeTracker(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private ServerState state = ServerState.Stopped;
    private TimeSpan completed;
    private long since;
    public ServerState State { get { lock (gate) return state; } }
    public TimeSpan Runtime
    {
        get { lock (gate) return completed + (Counts(state) ? time.GetElapsedTime(since) : TimeSpan.Zero); }
    }
    public void Transition(ServerState next)
    {
        if (!Enum.IsDefined(next)) throw new ArgumentOutOfRangeException(nameof(next));
        lock (gate)
        {
            var now = time.GetTimestamp();
            if (Counts(state)) completed += time.GetElapsedTime(since, now);
            since = now; state = next;
        }
    }
    private static bool Counts(ServerState value) => value is ServerState.Running or ServerState.Stopping;
}
