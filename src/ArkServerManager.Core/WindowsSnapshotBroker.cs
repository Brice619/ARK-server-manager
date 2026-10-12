using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace ArkServerManager.Core;

/// <summary>One explicitly prepared helper session; capture leases serialize its VSS scopes.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSnapshotBroker : IAsyncDisposable
{
    private readonly string helperExecutable;
    private readonly Func<ProcessStartInfo, Process?> launchProcess;
    public WindowsSnapshotBroker(string helperExecutable) : this(helperExecutable, Process.Start) { }
    internal WindowsSnapshotBroker(string helperExecutable, Func<ProcessStartInfo, Process?> launchProcess)
    { this.helperExecutable = helperExecutable; this.launchProcess = launchProcess; }
    private readonly SemaphoreSlim gate = new(1, 1);
    private NamedPipeServerStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private Process? helper;
    private bool disposed;

    public async Task<WindowsSnapshotCapability> InitializeAsync(bool requestElevation, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pipe?.IsConnected == true && helper is not null && !helper.HasExited)
                return new(true, true, true, "Snapshot helper ready.");
            CloseChannel();
            if (!Path.IsPathFullyQualified(helperExecutable) || !File.Exists(helperExecutable))
                throw new FileNotFoundException("The installed snapshot helper is missing.");
            RejectLinks(Path.GetFullPath(helperExecutable));
            var name = WindowsSnapshotPipe.NewName();
            pipe = WindowsSnapshotPipe.CreateServer(name);
            var start = new ProcessStartInfo(Path.GetFullPath(helperExecutable))
            {
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(helperExecutable))!,
                UseShellExecute = requestElevation,
                Verb = requestElevation ? "runas" : "",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(name);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            // Shell elevation may wait indefinitely for a Windows consent dialog.
            // Keep the manager responsive and close its channel if consent arrives too late.
            var launch = Task.Run(() => launchProcess(start), CancellationToken.None);
            try { helper = await launch.WaitAsync(deadline.Token) ?? throw new IOException("The snapshot helper could not start."); }
            catch
            {
                _ = launch.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) task.Result?.Dispose();
                    else _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            await pipe.WaitForConnectionAsync(deadline.Token);
            WindowsSnapshotPipe.RequireClient(pipe, helper.Id);
            reader = new(pipe, new UTF8Encoding(false), leaveOpen: true);
            writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var capability = await ReadAsync<WindowsSnapshotCapability>(deadline.Token);
            if (!capability.Windows || !capability.Elevated || !capability.ProviderLoaded)
                throw new IOException("Windows backup permissions have not been enabled.");
            return capability;
        }
        catch { CloseChannel(); throw; }
        finally { gate.Release(); }
    }

    public async Task<IStableServerState> AcquireAsync(string source, CancellationToken ct = default)
        => new Single(await AcquireSetAsync([source], ct), source);

    public async Task<IStableBackupSet> AcquireSetAsync(IReadOnlyList<string> sources, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        await gate.WaitAsync(ct);
        var transferred = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pipe?.IsConnected != true || helper is null || helper.HasExited)
                throw new InvalidOperationException("Enable Windows backup permissions before capturing running servers.");
            var selected = sources.Select(source => Path.TrimEndingDirectorySeparator(Path.GetFullPath(source))).ToArray();
            if (selected.Length == 0 || selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
                throw new ArgumentException("Choose distinct saved-state directories.");
            foreach (var source in selected)
            {
                if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Saved state is missing.");
                RejectLinks(source);
            }
            var request = new WindowsSnapshotRequest(1, selected.Select(source => Path.GetPathRoot(source)!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            await writer!.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
            var response = await ReadAsync<WindowsSnapshotResponse>(deadline.Token);
            var mapped = WindowsSnapshotSetProvider.ResolveDirectories(selected, response);
            if (mapped.Values.Any(directory => !Directory.Exists(directory)))
                throw new IOException("Saved state is missing from the captured volumes.");
            ct.ThrowIfCancellationRequested();
            transferred = true;
            return new Lease(this, response.Capture, mapped);
        }
        catch { CloseChannel(); throw; }
        finally { if (!transferred) gate.Release(); }
    }

    private async Task<T> ReadAsync<T>(CancellationToken ct)
    {
        var line = await reader!.ReadLineAsync(ct);
        if (line is null || line.Length > 65536) throw new IOException("The snapshot helper disconnected or returned an invalid response.");
        using var json = JsonDocument.Parse(line);
        if (json.RootElement.TryGetProperty("Error", out _)) throw new IOException("Windows snapshot capture failed.");
        return json.RootElement.Deserialize<T>() ?? throw new InvalidDataException("Missing snapshot response.");
    }

    private async ValueTask ReleaseAsync(Guid captureId)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await writer!.WriteLineAsync(("release:" + captureId.ToString("N")).AsMemory(), deadline.Token);
            var response = await ReadAsync<WindowsSnapshotReleaseResponse>(deadline.Token);
            if (response.SchemaVersion != 1 || response.CaptureId != captureId)
                throw new InvalidDataException("Snapshot release was not confirmed.");
        }
        catch { CloseChannel(); throw; }
        finally { gate.Release(); }
    }

    private void CloseChannel()
    {
        // Closing the last pipe handle gives the helper EOF and releases its COM-owned snapshots.
        var oldPipe = pipe; var oldReader = reader; var oldWriter = writer; var oldHelper = helper;
        pipe = null; reader = null; writer = null; helper = null;
        oldPipe?.Dispose();
        try { oldWriter?.Dispose(); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        finally { oldReader?.Dispose(); oldHelper?.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { disposed = true; CloseChannel(); }
        finally { gate.Release(); }
    }

    private static void RejectLinks(string target)
    {
        for (var path = target; path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Backup helper and state paths must not use links.");
    }

    private sealed class Single(IStableBackupSet set, string source) : IStableServerState
    {
        public string SourceDirectory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        public string Directory => set.GetState(source).Directory;
        public SnapshotCapture Capture => set.Capture;
        public ValueTask DisposeAsync() => set.DisposeAsync();
    }

    private sealed class Lease(WindowsSnapshotBroker owner, SnapshotCapture capture,
        IReadOnlyDictionary<string, string> mapped) : IStableBackupSet
    {
        private int disposed;
        public SnapshotCapture Capture => capture;
        public IStableServerState GetState(string sourceDirectory)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
            if (!mapped.TryGetValue(source, out var directory)) throw new ArgumentException("The source is outside this capture.");
            return new View(this, source, directory);
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) await owner.ReleaseAsync(capture.Id);
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
