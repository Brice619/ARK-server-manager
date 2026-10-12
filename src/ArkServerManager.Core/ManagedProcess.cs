using System.Diagnostics;
using System.Text;

namespace ArkServerManager.Core;

public sealed record ProcessLaunch(string Executable, string WorkingDirectory, IReadOnlyList<string> Arguments,
    string LogFile, IReadOnlyList<string>? Secrets = null)
{
    /// <summary>Child-only overrides; null removes an inherited variable.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentOverrides { get; init; } = new Dictionary<string, string?>();
}
public sealed record ProcessLine(DateTimeOffset Time, bool StandardError, string Text, long Sequence = 0);
public sealed record ProcessExit(int ExitCode, DateTimeOffset Time, bool RequestedStop);

/// <summary>Owns a process and persistent console capture. Process existence is not ARK readiness.</summary>
public sealed class ManagedProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly StreamWriter log;
    private readonly SemaphoreSlim logGate = new(1, 1);
    private readonly Queue<ProcessLine> tail = [];
    private readonly IReadOnlyList<string> secrets;
    private readonly Task stdout;
    private readonly Task stderr;
    private int requestedStop;
    private int disposed;
    private long consoleSequence;
    public int Id => process.Id;
    public bool IsAlive => !process.HasExited;
    public Task<ProcessExit> Completion { get; }
    public Exception? LogFailure { get; private set; }
    public IReadOnlyList<ProcessLine> ConsoleTail { get { lock (tail) return tail.ToArray(); } }
    public long ConsoleCheckpoint { get { lock (tail) return consoleSequence; } }

    /// <summary>Waits for new output from this owned process; previous-run logs and old tail entries cannot satisfy it.</summary>
    public async Task<ProcessLine> WaitForConsoleAsync(long afterSequence, Func<ProcessLine, bool> matches,
        TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (afterSequence < 0 || afterSequence > ConsoleCheckpoint) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                var lines = ConsoleTail;
                if (lines.Count > 0 && lines[0].Sequence > afterSequence + 1)
                    throw new IOException("Console output was omitted before confirmation could be observed.");
                foreach (var line in lines.Where(line => line.Sequence > afterSequence))
                {
                    if (matches(line)) return line;
                    afterSequence = line.Sequence;
                }
                if (Completion.IsCompleted) throw new IOException("The server exited before confirmation was observed.");
                await Task.Delay(25, linked.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("The server did not confirm the operation in time."); }
    }

    private ManagedProcess(Process process, StreamWriter log, IReadOnlyList<string> secrets)
    {
        this.process = process;
        this.log = log;
        this.secrets = secrets.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length).ToArray();
        stdout = CaptureAsync(process.StandardOutput, false);
        stderr = CaptureAsync(process.StandardError, true);
        Completion = ObserveExitAsync();
    }

    public static ManagedProcess Start(ProcessLaunch launch)
    {
        var executable = Path.GetFullPath(launch.Executable);
        var directory = Path.GetFullPath(launch.WorkingDirectory);
        if (!File.Exists(executable)) throw new FileNotFoundException("The executable is missing.", executable);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (var argument in launch.Arguments) info.ArgumentList.Add(argument);
        foreach (var item in launch.EnvironmentOverrides)
        {
            if (string.IsNullOrEmpty(item.Key) || item.Key.Contains('=') || item.Key.Contains('\0') || item.Value?.Contains('\0') == true)
                throw new ArgumentException("Choose valid child-process environment variables.");
            if (item.Value is null) info.Environment.Remove(item.Key);
            else info.Environment[item.Key] = item.Value;
        }
        var logPath = Path.GetFullPath(launch.LogFile);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException("The process could not start.");
            return new(process, log, launch.Secrets ?? []);
        }
        catch { process.Dispose(); log.Dispose(); throw; }
    }

    public async Task SendStandardInputAsync(string text, CancellationToken cancellationToken = default)
    {
        if (text.Contains('\r') || text.Contains('\n')) throw new ArgumentException("Send one input line at a time.");
        await process.StandardInput.WriteLineAsync(text.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    public async Task<bool> RequestStopAsync(Func<CancellationToken, Task> gracefulShutdown,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (process.HasExited) return true;
        Interlocked.Exchange(ref requestedStop, 1);
        try { await gracefulShutdown(cancellationToken); }
        catch { Interlocked.Exchange(ref requestedStop, 0); throw; }
        try
        {
            await Completion.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            // A shutdown timeout must not silently force-kill a world that may still be saving.
            Interlocked.Exchange(ref requestedStop, 0);
            return false;
        }
        catch (OperationCanceledException) { Interlocked.Exchange(ref requestedStop, 0); throw; }
    }

    private async Task CaptureAsync(StreamReader reader, bool error)
    {
        while (await reader.ReadLineAsync() is { } text)
        {
            foreach (var secret in secrets) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
            ProcessLine line;
            lock (tail)
            {
                line = new(DateTimeOffset.UtcNow, error, text, ++consoleSequence);
                tail.Enqueue(line);
                while (tail.Count > 500) tail.Dequeue();
            }
            await logGate.WaitAsync();
            try
            {
                if (LogFailure is null)
                {
                    try { await log.WriteLineAsync($"{line.Time:O} {(error ? "stderr" : "stdout")} {line.Text}"); }
                    catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                    {
                        // Keep draining pipes so a full log drive does not freeze the game process.
                        LogFailure = failure;
                    }
                }
            }
            finally { logGate.Release(); }
        }
    }

    private async Task<ProcessExit> ObserveExitAsync()
    {
        await process.WaitForExitAsync();
        try { await Task.WhenAll(stdout, stderr); }
        finally { await log.DisposeAsync(); }
        return new(process.ExitCode, DateTimeOffset.UtcNow, Volatile.Read(ref requestedStop) != 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Owners must stop gracefully or retain this instance in the background service.
        // Explicit disposal of a live child is the last-resort cleanup path.
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        try { await Completion; }
        finally { await log.DisposeAsync(); process.Dispose(); logGate.Dispose(); }
    }
}
