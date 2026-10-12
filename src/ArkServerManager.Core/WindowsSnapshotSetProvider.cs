using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;

namespace ArkServerManager.Core;

/// <summary>Owns a nonpersistent multi-volume VSS helper until all captured directories have been archived.</summary>
public sealed class WindowsSnapshotSetProvider
{
    private readonly string executable;
    public WindowsSnapshotSetProvider(string helperExecutable)
    {
        if (!Path.IsPathFullyQualified(helperExecutable)) throw new ArgumentException("Choose the installed snapshot helper.");
        executable = Path.GetFullPath(helperExecutable);
    }

    public static IReadOnlyDictionary<string, string> ResolveDirectories(IReadOnlyList<string> sources, WindowsSnapshotResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var selected = NormalizeSources(sources);
        if (response.SchemaVersion != 1 || response.Capture is null || response.Capture.Id == Guid.Empty
            || response.Capture.CapturedAt == default || response.Volumes is null)
            throw new InvalidDataException("Invalid snapshot-set response.");
        var expectedVolumes = selected.Select(source => Path.GetPathRoot(source)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var volumes = new Dictionary<string, WindowsSnapshotVolume>(StringComparer.OrdinalIgnoreCase);
        foreach (var volume in response.Volumes)
        {
            if (volume is null || volume.SnapshotId == Guid.Empty || !expectedVolumes.Contains(volume.Volume, StringComparer.OrdinalIgnoreCase)
                || !volumes.TryAdd(volume.Volume, volume))
                throw new InvalidDataException("Unexpected or repeated snapshot volume.");
        }
        if (volumes.Count != expectedVolumes.Length) throw new InvalidDataException("A selected volume is missing from the snapshot set.");
        if (volumes.Values.GroupBy(volume => volume.SnapshotId).Any(group => group.Select(volume => volume.DeviceObject)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            || volumes.Values.GroupBy(volume => volume.DeviceObject, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Select(volume => volume.SnapshotId).Distinct().Count() != 1))
            throw new InvalidDataException("Inconsistent snapshot device identities.");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in selected)
            result.Add(source, WindowsVolumeSnapshot.ResolveDirectory(source, volumes[Path.GetPathRoot(source)!].DeviceObject));
        return new ReadOnlyDictionary<string, string>(result);
    }

    private static string[] NormalizeSources(IReadOnlyList<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("VSS capture requires Windows.");
        if (sources.Count == 0) throw new ArgumentException("Select state directories to capture.");
        var normalized = sources.Select(source => Path.TrimEndingDirectorySeparator(Path.GetFullPath(source))).ToArray();
        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new ArgumentException("Capture sources must be distinct.");
        foreach (var source in normalized)
            _ = WindowsVolumeSnapshot.ResolveDirectory(source, @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1");
        return normalized;
    }

    public async Task<IStableServerState> AcquireAsync(string source, CancellationToken ct = default)
    {
        var set = await AcquireSetAsync([source], ct);
        return new OwnedSingleView(set, source);
    }

    public async Task<IStableBackupSet> AcquireSetAsync(IReadOnlyList<string> sources, CancellationToken ct = default)
    {
        var selected = NormalizeSources(sources);
        foreach (var source in selected)
        {
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("A selected state directory is missing.");
            RejectLinks(source);
        }
        if (!File.Exists(executable)) throw new FileNotFoundException("The required snapshot helper is not installed.");
        RejectLinks(executable);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        ct.ThrowIfCancellationRequested();
        var process = Process.Start(start) ?? throw new IOException("The snapshot helper could not start.");
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            var request = new WindowsSnapshotRequest(1, selected.Select(source => Path.GetPathRoot(source)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            await process.StandardInput.FlushAsync();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            string? line;
            try { line = await process.StandardOutput.ReadLineAsync(deadline.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("Windows snapshot-set creation timed out."); }
            if (line is null || line.Length > 65536) throw new InvalidDataException("The snapshot helper returned no valid response.");
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("Error", out _))
                throw new IOException("Windows could not capture the selected volumes. Check backup permissions and required components.");
            var response = json.RootElement.Deserialize<WindowsSnapshotResponse>()
                ?? throw new InvalidDataException("Missing snapshot-set response.");
            var mapped = ResolveDirectories(selected, response);
            if (mapped.Values.Any(path => !Directory.Exists(path))) throw new IOException("A state directory is missing in the captured volumes.");
            ct.ThrowIfCancellationRequested();
            return new Set(process, errors, response.Capture, mapped);
        }
        catch
        {
            try { await ReleaseAsync(process, errors, TimeSpan.FromSeconds(5)); }
            catch (Exception cleanup) when (cleanup is IOException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }
    }

    private static void RejectLinks(string target)
    {
        for (var path = target; path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Snapshot helper and source paths must not use links.");
    }

    private static async Task ReleaseAsync(Process process, Task<string> errors, TimeSpan timeout)
    {
        try
        {
            process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(timeout);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                // Only the owned helper is terminated. Its nonpersistent COM-owned views auto-release.
                process.Kill();
                using var killedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(killedDeadline.Token); }
                catch (OperationCanceledException) { throw new TimeoutException("Snapshot helper termination could not be confirmed."); }
                throw new TimeoutException("The snapshot helper exceeded its release deadline and was terminated.");
            }
            _ = await errors;
            if (process.ExitCode != 0) throw new IOException("The snapshot helper did not complete normally.");
        }
        finally { process.Dispose(); }
    }

    private sealed class OwnedSingleView(IStableBackupSet set, string source) : IStableServerState
    {
        public string SourceDirectory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        public string Directory => set.GetState(source).Directory;
        public SnapshotCapture Capture => set.Capture;
        public ValueTask DisposeAsync() => set.DisposeAsync();
    }

    private sealed class Set(Process process, Task<string> errors, SnapshotCapture capture,
        IReadOnlyDictionary<string, string> mapped) : IStableBackupSet
    {
        private int disposed;
        public SnapshotCapture Capture => capture;
        public IStableServerState GetState(string sourceDirectory)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
            if (!mapped.TryGetValue(source, out var directory)) throw new ArgumentException("This source is not in the captured set.");
            return new View(this, source, directory);
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) await ReleaseAsync(process, errors, TimeSpan.FromSeconds(30));
        }
        private sealed class View(Set owner, string source, string directory) : IStableServerState
        {
            public string SourceDirectory => source;
            public SnapshotCapture Capture => owner.Capture;
            public string Directory { get { ObjectDisposedException.ThrowIf(Volatile.Read(ref owner.disposed) != 0, owner); return directory; } }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
