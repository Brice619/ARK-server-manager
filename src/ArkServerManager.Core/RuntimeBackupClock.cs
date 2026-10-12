namespace ArkServerManager.Core;

public sealed record BackupSlot(long Hour, TimeSpan Runtime);

/// <summary>Counts running time only. Persistence and scheduling supply the elapsed running duration.</summary>
public sealed class RuntimeBackupClock
{
    public TimeSpan Runtime { get; private set; }
    public long CompletedHours => Runtime.Ticks / TimeSpan.TicksPerHour;
    public long OldestRetainedHour => Math.Max(1, CompletedHours - 9);

    public RuntimeBackupClock(TimeSpan runtime = default)
    {
        if (runtime < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(runtime));
        Runtime = runtime;
    }

    public IReadOnlyList<BackupSlot> Advance(TimeSpan elapsed, bool running)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (!running) return [];
        var before = CompletedHours;
        Runtime += elapsed;
        var after = CompletedHours;
        // Missed historical snapshots cannot be reconstructed. Return the currently due slot only.
        return after > before ? [new(after, TimeSpan.FromHours(after))] : [];
    }

    public bool ShouldRetain(long backupHour) => backupHour >= OldestRetainedHour && backupHour <= CompletedHours;
}
