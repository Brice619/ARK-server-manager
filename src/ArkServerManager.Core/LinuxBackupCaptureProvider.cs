using System.Runtime.Versioning;
namespace ArkServerManager.Core;

[SupportedOSPlatform("linux")]
public sealed class LinuxBackupCaptureProvider(string dependenciesDirectory) : IAsyncDisposable
{
    private readonly string snapshotStorage = Path.Combine(dependenciesDirectory, "LinuxSnapshots");
    private readonly string copyStorage = Path.Combine(dependenciesDirectory, "LinuxCaptures");
    private readonly BtrfsSnapshotProvider snapshots = new(Path.Combine(dependenciesDirectory, "LinuxSnapshots"));
    private readonly LinuxVerifiedCopyProvider copies = new(Path.Combine(dependenciesDirectory, "LinuxCaptures"));

    public async Task CleanupPendingAsync(CancellationToken ct = default)
    {
        await snapshots.CleanupPendingAsync(ct);
        await copies.CleanupPendingAsync(ct);
    }
    public async Task<IStableServerState> AcquireAsync(string source, CancellationToken ct = default)
        => new Single(await AcquireSetAsync([source], ct), source);
    public async Task<IStableBackupSet> AcquireSetAsync(IReadOnlyList<string> sources, CancellationToken ct = default)
    {
        var selected = sources.Select(source => Path.TrimEndingDirectorySeparator(Path.GetFullPath(source))).ToArray();
        var mounts = LinuxMountTopology.Parse(await File.ReadAllTextAsync("/proc/self/mountinfo", ct));
        return LinuxBackupCapturePlan.Select(selected, Path.GetFullPath(snapshotStorage), Path.GetFullPath(copyStorage), mounts)
            == LinuxBackupCaptureKind.BtrfsSnapshot ? await snapshots.AcquireSetAsync(selected, ct) : await copies.AcquireSetAsync(selected, ct);
    }
    public async ValueTask DisposeAsync()
    { try { await snapshots.DisposeAsync(); } finally { await copies.DisposeAsync(); } }
    private sealed class Single(IStableBackupSet set, string source) : IStableServerState
    {
        public string SourceDirectory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        public string Directory => set.GetState(source).Directory;
        public SnapshotCapture Capture => set.Capture;
        public ValueTask DisposeAsync() => set.DisposeAsync();
    }
}
