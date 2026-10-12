namespace ArkServerManager.Core;

public sealed record NotificationPreferences
{
    public int SchemaVersion { get; init; } = 1;
    public bool InApp { get; init; } = true;
    public bool OperatingSystem { get; init; } = true;
    public bool WarningsAndErrors { get; init; } = true;
    public bool CompletedOperations { get; init; }
    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Unsupported notification preferences.");
    }
}

/// <summary>Alerts only for new activity observed after this app session connects.</summary>
public sealed class ActivityNotificationTracker
{
    private readonly HashSet<Guid> observed = [];
    private readonly Queue<Guid> observationOrder = new();
    private bool initialized;
    public IReadOnlyList<ActivityEntry> Observe(ActivityHistory history, NotificationPreferences preferences)
    {
        preferences.Validate();
        if (history.Entries is null || history.Entries.Any(e => e is null || e.Id == Guid.Empty || !Enum.IsDefined(e.Severity)))
            throw new InvalidDataException("Invalid notification activity.");
        var candidates = history.Entries.Where(e => !observed.Contains(e.Id))
            .OrderBy(e => e.Time).ThenBy(e => e.Id).ToArray();
        foreach (var entry in history.Entries)
        {
            if (observed.Add(entry.Id)) observationOrder.Enqueue(entry.Id);
        }
        // Track identities rather than timestamps so correcting the host clock
        // cannot silently discard a new failure. Keep more than the bounded
        // activity window without accumulating memory throughout a long session.
        while (observationOrder.Count > 2048) observed.Remove(observationOrder.Dequeue());
        if (!initialized) { initialized = true; return []; }
        return candidates.Where(e => e.Severity is ActivitySeverity.Warning or ActivitySeverity.Error
            ? preferences.WarningsAndErrors : preferences.CompletedOperations && e.OperationId is not null).ToArray();
    }
}
