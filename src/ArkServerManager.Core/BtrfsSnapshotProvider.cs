using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;

namespace ArkServerManager.Core;

public sealed record BtrfsCaptureRecord(int SchemaVersion, Guid OperationId, Guid SourceUuid, Guid? SnapshotUuid)
{
    public int OwnerProcessId { get; init; }
    public long OwnerStartedTicks { get; init; }
    public Guid OwnerBootId { get; init; }
}

/// <summary>One read-only subvolume snapshot, with durable ownership for interrupted cleanup.</summary>
[SupportedOSPlatform("linux")]
public sealed class BtrfsSnapshotProvider(string storageDirectory) : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = Gates.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageDirectory)), _ => new(1, 1));
    private bool disposed;

    public async Task CleanupPendingAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var storage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageDirectory));
            RejectLinks(storage);
            if (!Directory.Exists(storage) || !Directory.EnumerateFiles(storage, "*.json").Any()) return;
            var mounts = LinuxMountTopology.Parse(await File.ReadAllTextAsync("/proc/self/mountinfo", ct));
            if (LinuxMountTopology.Resolve(storage, mounts).FileSystem != "btrfs")
                throw new NotSupportedException("Recorded snapshot storage is no longer on Btrfs; preserve it for review.");
            var executable = new[] { "/usr/bin/btrfs", "/usr/sbin/btrfs" }.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("The Btrfs tool required for snapshot cleanup is missing.");
            await CleanupRecordedAsync(storage, executable, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<IStableServerState> AcquireAsync(string source, CancellationToken ct = default)
        => new Single(await AcquireSetAsync([source], ct), source);

    public async Task<IStableBackupSet> AcquireSetAsync(IReadOnlyList<string> sources, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        var transferred = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var selected = sources.Select(s => Path.TrimEndingDirectorySeparator(Path.GetFullPath(s))).ToArray();
            var storage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageDirectory));
            var mounts = LinuxMountTopology.Parse(await File.ReadAllTextAsync("/proc/self/mountinfo", ct));
            var mount = LinuxMountTopology.RequireSingleBtrfsMount(selected, storage, mounts);
            RejectLinks(storage);
            foreach (var source in selected)
            {
                if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Saved-state storage is missing.");
                RejectLinks(source);
            }
            var executable = new[] { "/usr/bin/btrfs", "/usr/sbin/btrfs" }.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("Install the required Btrfs tools before capturing running servers.");
            var original = BtrfsSubvolume.Parse(await RunAsync(executable, ["subvolume", "show", mount.Directory], ct));
            Directory.CreateDirectory(storage); RejectLinks(storage);
            foreach (var path in selected.Append(storage))
            {
                var rootId = await RunAsync(executable, ["inspect-internal", "rootid", path], ct);
                if (!ulong.TryParse(rootId.Trim(), out var id) || id != original.Id)
                    throw new NotSupportedException("Saved state crosses nested Btrfs subvolumes; a coordinated capture is required.");
            }
            var subvolumes = BtrfsSubvolume.ListedFileSystemPaths(await RunAsync(executable, ["subvolume", "list", "-a", mount.Directory], ct));
            foreach (var subvolume in subvolumes.Where(path => path != mount.Root && LinuxMountTopology.Contains(mount.Root, path)))
            {
                var nested = Path.Combine(mount.Directory, Path.GetRelativePath(mount.Root, subvolume));
                if (selected.Any(source => LinuxMountTopology.Contains(source, nested)))
                    throw new NotSupportedException("Saved state contains nested Btrfs subvolumes that a parent snapshot would omit.");
            }
            await CleanupRecordedAsync(storage, executable, ct);
            var operation = Guid.NewGuid();
            var snapshot = Path.Combine(storage, operation.ToString("N"));
            var journal = Path.Combine(storage, operation.ToString("N") + ".json");
            if (Path.Exists(snapshot) || Path.Exists(journal)) throw new IOException("The snapshot destination is already occupied.");
            var owner = await LinuxProcessIdentity.ReadAsync(Environment.ProcessId, ct)
                ?? throw new IOException("The snapshot manager identity could not be recorded.");
            var record = new BtrfsCaptureRecord(2, operation, original.Uuid, null)
            { OwnerProcessId = owner.ProcessId, OwnerStartedTicks = owner.StartedTicks, OwnerBootId = owner.BootId };
            await new AtomicJsonStore<BtrfsCaptureRecord>(journal).SaveAsync(record, ct);
            await RunAsync(executable, ["subvolume", "snapshot", "-r", mount.Directory, snapshot], ct);
            RejectLinks(snapshot);
            var captured = BtrfsSubvolume.Parse(await RunAsync(executable, ["subvolume", "show", snapshot], ct));
            RequireOwned(captured, record);
            record = record with { SnapshotUuid = captured.Uuid };
            await new AtomicJsonStore<BtrfsCaptureRecord>(journal).SaveAsync(record, ct);
            var mapped = selected.ToDictionary(source => source, source => Path.Combine(snapshot, Path.GetRelativePath(mount.Directory, source)), StringComparer.Ordinal);
            foreach (var path in mapped.Values)
            {
                if (!Directory.Exists(path)) throw new IOException("Saved state is missing from its Btrfs snapshot.");
                RejectLinks(path);
            }
            ct.ThrowIfCancellationRequested();
            transferred = true;
            return new Lease(this, new(captured.Uuid, DateTimeOffset.UtcNow), mapped,
                () => CleanupOneAsync(storage, executable, record, CancellationToken.None));
        }
        finally { if (!transferred) gate.Release(); }
    }

    private static async Task CleanupRecordedAsync(string storage, string executable, CancellationToken ct)
    {
        var count = 0;
        foreach (var journal in Directory.EnumerateFiles(storage, "*.json"))
        {
            if (++count > 1000) throw new InvalidDataException("Too many interrupted Btrfs captures; review backup storage.");
            RejectLinks(journal);
            var record = await CoordinatedBackupRecords.ReadBoundedAsync<BtrfsCaptureRecord>(journal, ct)
                ?? throw new InvalidDataException("A Btrfs capture record disappeared.");
            if (record.SchemaVersion is not (1 or 2) || record.OperationId == Guid.Empty || record.SourceUuid == Guid.Empty
                || record.SnapshotUuid == Guid.Empty || record.OwnerProcessId <= 0 || record.OwnerStartedTicks < 0
                || record.SchemaVersion == 2 && record.OwnerBootId == Guid.Empty
                || Path.GetFileName(journal) != record.OperationId.ToString("N") + ".json")
                throw new InvalidDataException("Unexpected Btrfs capture ownership record.");
            var owner = await LinuxProcessIdentity.ReadAsync(record.OwnerProcessId, ct);
            if (owner?.Alive == true && record.SchemaVersion == 1)
                throw new IOException("An older snapshot owner identity needs review before cleanup.");
            if (record.OwnerProcessId != Environment.ProcessId
                && owner?.Matches(record.OwnerProcessId, record.OwnerBootId, record.OwnerStartedTicks) == true)
                throw new IOException("Another manager still owns this Btrfs capture.");
            await CleanupOneAsync(storage, executable, record, ct);
        }
    }

    private static async Task CleanupOneAsync(string storage, string executable, BtrfsCaptureRecord record, CancellationToken ct)
    {
        var snapshot = Path.Combine(storage, record.OperationId.ToString("N"));
        RejectLinks(snapshot);
        if (File.Exists(snapshot)) throw new IOException("A file occupies the recorded Btrfs snapshot path.");
        if (Directory.Exists(snapshot))
        {
            var metadata = BtrfsSubvolume.Parse(await RunAsync(executable, ["subvolume", "show", snapshot], ct));
            RequireOwned(metadata, record);
            await RunAsync(executable, ["subvolume", "delete", "--commit-after", snapshot], ct);
            if (Path.Exists(snapshot)) throw new IOException("Btrfs snapshot removal was not confirmed.");
        }
        var journal = Path.Combine(storage, record.OperationId.ToString("N") + ".json");
        RejectLinks(journal); File.Delete(journal);
    }

    private static void RequireOwned(BtrfsSubvolume metadata, BtrfsCaptureRecord record)
    {
        metadata.RequireSnapshotIdentity(record.SourceUuid, record.SnapshotUuid);
    }

    private static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The Btrfs tool could not start.");
        try
        {
            var output = ReadLimitedAsync(process.StandardOutput, deadline.Token);
            var errors = ReadLimitedAsync(process.StandardError, deadline.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(deadline.Token));
            if (process.ExitCode != 0) throw new IOException("Btrfs capture or cleanup failed. Check filesystem support and backup permissions.");
            return await output;
        }
        catch
        {
            if (!process.HasExited) process.Kill();
            using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(exitDeadline.Token);
            throw;
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder(); var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (text.Length + count > 1024 * 1024) throw new InvalidDataException("Btrfs command output exceeded its limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static void RejectLinks(string target)
    {
        for (var current = target; current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Btrfs capture paths must not use links.");
    }

    public async ValueTask DisposeAsync()
    { await gate.WaitAsync(); try { disposed = true; } finally { gate.Release(); } }

    private sealed class Single(IStableBackupSet set, string source) : IStableServerState
    {
        public string SourceDirectory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        public string Directory => set.GetState(source).Directory;
        public SnapshotCapture Capture => set.Capture;
        public ValueTask DisposeAsync() => set.DisposeAsync();
    }

    private sealed class Lease(BtrfsSnapshotProvider owner, SnapshotCapture capture,
        IReadOnlyDictionary<string, string> mapped, Func<Task> cleanup) : IStableBackupSet
    {
        private int disposed;
        public SnapshotCapture Capture => capture;
        public IStableServerState GetState(string sourceDirectory)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
            if (!mapped.TryGetValue(source, out var directory)) throw new ArgumentException("This source is outside the captured set.");
            return new View(this, source, directory);
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { await cleanup(); } finally { owner.gate.Release(); }
        }
        private sealed class View(Lease owner, string source, string directory) : IStableServerState
        {
            public string SourceDirectory => source;
            public SnapshotCapture Capture => owner.Capture;
            public string Directory { get { ObjectDisposedException.ThrowIf(Volatile.Read(ref owner.disposed) != 0, owner); return directory; } }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

