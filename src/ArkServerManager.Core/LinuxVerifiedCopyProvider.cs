using System.Runtime.Versioning;
namespace ArkServerManager.Core;

public sealed record LinuxCopyOwnership(int SchemaVersion, Guid Id, LinuxProcessIdentity Owner);

/// <summary>Private copy capture accepted only when every source remains unchanged across the whole copy interval.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxVerifiedCopyProvider(string storageDirectory) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageDirectory));
    private bool disposed;

    public async Task CleanupPendingAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { ObjectDisposedException.ThrowIf(disposed, this); await CleanupAsync(ct); }
        finally { gate.Release(); }
    }

    public async Task<IStableBackupSet> AcquireSetAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        var transferred = false;
        Guid? owned = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RejectLinks(storage);
            Directory.CreateDirectory(storage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await CleanupAsync(ct);
            var roots = paths.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToArray();
            if (roots.Length is < 1 or > 129 || roots.Distinct(StringComparer.Ordinal).Count() != roots.Length)
                throw new ArgumentException("Choose distinct saved-state sources.");
            var mounts = LinuxMountTopology.Parse(await File.ReadAllTextAsync("/proc/self/mountinfo", ct));
            foreach (var source in roots)
            {
                RejectLinks(source);
                if (LinuxMountTopology.Contains(source, storage) || LinuxMountTopology.Contains(storage, source))
                    throw new ArgumentException("Capture storage must be separate from saved state.");
                var mount = LinuxMountTopology.Resolve(source, mounts);
                if (mount.FileSystem is not ("ext4" or "xfs") || mounts.Any(m => m.Id != mount.Id && LinuxMountTopology.Contains(source, m.Directory)))
                    throw new NotSupportedException("Verified-copy capture requires local ext4 or XFS saved state without nested mounts.");
            }
            var ancestry = new Dictionary<string, LinuxCaptureFileIdentity>(StringComparer.Ordinal);
            foreach (var source in roots)
                for (var parent = Path.GetDirectoryName(source); parent is not null; parent = Path.GetDirectoryName(parent))
                {
                    var identity = LinuxCaptureMetadata.Read(parent);
                    if (!identity.IsDirectory) throw new IOException("A saved-state ancestor is not a directory.");
                    ancestry.TryAdd(parent, identity);
                }
            var id = Guid.NewGuid();
            var directory = Path.Combine(storage, id.ToString("N"));
            var journal = Path.Combine(storage, id.ToString("N") + ".json");
            if (Path.Exists(directory) || Path.Exists(journal)) throw new IOException("Capture destination is occupied.");
            var owner = await LinuxProcessIdentity.ReadAsync(Environment.ProcessId, ct) ?? throw new IOException("Capture owner identity is unavailable.");
            await new AtomicJsonStore<LinuxCopyOwnership>(journal, privateFile: true).SaveAsync(new(1, id, owner), ct);
            owned = id;
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var selected = roots.Select((source, index) => new StableCaptureSource(source, Path.Combine(directory, index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Path.GetFileName(source) == "Saved" ? new[] { "SavedArks", "SaveGames" } : null)).ToArray();
            var capture = await StableCaptureCoordinator.CopyAsync(selected,
                (source, token) =>
                {
                    RejectLinks(source.SourceDirectory);
                    return Task.FromResult(LinuxCaptureInventoryReader.Read(source.SourceDirectory, source.SelectedDirectories, token));
                },
                async (source, entry, token) =>
                {
                    RejectLinks(source.SourceDirectory);
                    var destination = entry.RelativePath.Length == 0 ? source.DestinationDirectory : Path.Combine(source.DestinationDirectory, entry.RelativePath);
                    RejectLinks(destination);
                    if (entry.IsDirectory) Directory.CreateDirectory(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    else await LinuxCaptureFile.CopyAsync(Path.Combine(source.SourceDirectory, entry.RelativePath), destination,
                        new(false, entry.Bytes, entry.Revision), token);
                }, ct);
            foreach (var ancestor in ancestry)
                if (LinuxCaptureMetadata.Read(ancestor.Key) != ancestor.Value)
                    throw new IOException("A saved-state ancestor changed during capture. Discard this capture and retry.");
            var finalMounts = LinuxMountTopology.Parse(await File.ReadAllTextAsync("/proc/self/mountinfo", ct));
            foreach (var source in roots)
                if (LinuxMountTopology.Resolve(source, finalMounts) != LinuxMountTopology.Resolve(source, mounts)
                    || finalMounts.Any(m => m.Id != LinuxMountTopology.Resolve(source, mounts).Id && LinuxMountTopology.Contains(source, m.Directory)))
                    throw new IOException("Saved-state mounted storage changed during capture. Discard this capture and retry.");
            ct.ThrowIfCancellationRequested();
            transferred = true;
            return new Lease(capture, selected.ToDictionary(s => s.SourceDirectory, s => s.DestinationDirectory, StringComparer.Ordinal),
                async () => { try { Remove(id); } finally { gate.Release(); } });
        }
        finally
        {
            if (!transferred)
            {
                try { if (owned.HasValue) Remove(owned.Value); }
                finally { gate.Release(); }
            }
        }
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        RejectLinks(storage);
        if (!Directory.Exists(storage)) return;
        var count = 0;
        foreach (var journal in Directory.EnumerateFiles(storage, "*.json"))
        {
            if (++count > 1000) throw new IOException("Too many interrupted captures; review capture storage.");
            RejectLinks(journal);
            var record = await CoordinatedBackupRecords.ReadBoundedAsync<LinuxCopyOwnership>(journal, ct)
                ?? throw new IOException("Capture ownership record disappeared.");
            if (record.SchemaVersion != 1 || record.Id == Guid.Empty || record.Owner is null || record.Owner.ProcessId <= 0
                || record.Owner.BootId == Guid.Empty || record.Owner.StartedTicks < 0 || Path.GetFileName(journal) != record.Id.ToString("N") + ".json")
                throw new InvalidDataException("Unexpected capture ownership record.");
            var owner = await LinuxProcessIdentity.ReadAsync(record.Owner.ProcessId, ct);
            if (owner?.Matches(record.Owner.ProcessId, record.Owner.BootId, record.Owner.StartedTicks) == true)
                throw new IOException("A manager still owns this capture; preserve it until that operation finishes.");
            Remove(record.Id);
        }
    }

    private void Remove(Guid id)
    {
        var directory = Path.Combine(storage, id.ToString("N"));
        RejectLinks(directory);
        if (Directory.Exists(directory))
        {
            // Traverse manually so a linked directory is rejected before following it.
            RemoveDirectory(directory);
        }
        else if (File.Exists(directory)) throw new IOException("A file replaced the capture directory.");
        var journal = Path.Combine(storage, id.ToString("N") + ".json");
        RejectLinks(journal); File.Delete(journal);
    }
    private static void RemoveDirectory(string directory)
    {
        RejectLinks(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinks(entry);
            if (Directory.Exists(entry)) RemoveDirectory(entry); else File.Delete(entry);
        }
        Directory.Delete(directory);
    }
    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Capture paths must not use links.");
    }
    public async ValueTask DisposeAsync()
    { await gate.WaitAsync(); try { disposed = true; } finally { gate.Release(); } }

    private sealed class Lease(SnapshotCapture capture, IReadOnlyDictionary<string, string> mapped, Func<Task> cleanup) : IStableBackupSet
    {
        private int disposed;
        public SnapshotCapture Capture => capture;
        public IStableServerState GetState(string sourceDirectory)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
            if (!mapped.TryGetValue(source, out var path)) throw new ArgumentException("Source is outside this capture.");
            return new View(this, source, path);
        }
        public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref disposed, 1) == 0) await cleanup(); }
        private sealed class View(Lease owner, string source, string path) : IStableServerState
        {
            public string SourceDirectory => source;
            public SnapshotCapture Capture => owner.Capture;
            public string Directory { get { ObjectDisposedException.ThrowIf(Volatile.Read(ref owner.disposed) != 0, owner); return path; } }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
