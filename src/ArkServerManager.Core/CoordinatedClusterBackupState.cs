namespace ArkServerManager.Core;

/// <summary>One cluster capture lifetime. Dispose only after all member and shared archives finish.</summary>
public sealed class CoordinatedClusterBackupState : IAsyncDisposable
{
    private readonly IStableBackupSet captured;
    private readonly SemaphoreSlim? lifecycleGate;
    private int disposed;
    public CatalogDocument Catalog { get; }
    public Guid ClusterId { get; }
    public ClusterStorageLocations SharedStorage { get; }
    public SnapshotCapture Capture => captured.Capture;
    internal CoordinatedClusterBackupState(CatalogDocument catalog, Guid clusterId,
        ClusterStorageLocations storage, IStableBackupSet captured, SemaphoreSlim? lifecycleGate)
    {
        Catalog = catalog; ClusterId = clusterId; SharedStorage = storage;
        this.captured = captured; this.lifecycleGate = lifecycleGate;
    }
    public IStableServerState GetServerState(Guid serverId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var server = Catalog.Servers.SingleOrDefault(s => s.Id == serverId && s.ClusterId == ClusterId)
            ?? throw new ArgumentException("Choose a member of the captured cluster.");
        return captured.GetState(Path.Combine(server.Directory, "ShooterGame", "Saved"));
    }
    public IStableServerState GetSharedState()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return captured.GetState(SharedStorage.Transfers);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { await captured.DisposeAsync(); }
        finally { lifecycleGate?.Release(); }
    }
}
