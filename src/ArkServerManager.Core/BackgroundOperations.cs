using System.Text.Json;

namespace ArkServerManager.Core;

public enum BackgroundOperationState { Queued, Running, Succeeded, Failed, Canceled }
public sealed record OperationProgress(string Message, int? Percent = null);
public sealed record BackgroundOperation(Guid Id, string Kind, BackgroundOperationState State,
    DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, OperationProgress? Progress, JsonElement? Result, string? Error,
    string? ReportingError = null);
public sealed record OperationStatusRequest(Guid Id);
public sealed record OperationLookup(BackgroundOperation? Operation);

/// <summary>Host-lifetime work survives client disconnections. Results are bounded and are not durable across host restarts.</summary>
public sealed class BackgroundOperations : IAsyncDisposable
{
    private sealed class Entry(BackgroundOperation snapshot, string fingerprint)
    {
        public BackgroundOperation Snapshot = snapshot;
        public readonly string Fingerprint = fingerprint;
        public Task Work = Task.CompletedTask;
    }
    private readonly object sync = new();
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly SemaphoreSlim workers;
    private readonly CancellationTokenSource lifetime = new();
    private readonly int capacity;
    private readonly Func<BackgroundOperation, Task>? recordCompletion;
    private bool disposed;
    private bool quiesced;

    public bool TryQuiesce()
    {
        lock (sync)
        {
            if (disposed || entries.Values.Any(e => !e.Work.IsCompleted)) return false;
            quiesced = true;
            return true;
        }
    }

    public BackgroundOperations(int capacity, int parallelism, Func<BackgroundOperation, Task>? recordCompletion = null)
    {
        if (capacity < 1 || parallelism < 1 || parallelism > capacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        this.recordCompletion = recordCompletion;
        workers = new(parallelism, parallelism);
    }

    public BackgroundOperation Submit(Guid id, string kind, JsonElement? payload,
        Func<Action<OperationProgress>, CancellationToken, Task<object>> run)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("An operation identity and kind are required.");
        ArgumentNullException.ThrowIfNull(run);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(kind + "\n" + payload?.GetRawText())));
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (quiesced) throw new InvalidOperationException("The manager is preparing to close for an app update.");
            if (entries.TryGetValue(id, out var existing))
            {
                if (existing.Fingerprint != fingerprint) throw new ArgumentException("This request identity was already used for another operation.");
                return existing.Snapshot;
            }
            if (entries.Count >= capacity)
            {
                // Never evict unfinished work or claim a missing result means success.
                var oldest = entries.Values.Where(e => e.Work.IsCompleted && e.Snapshot.FinishedAt.HasValue)
                    .OrderBy(e => e.Snapshot.FinishedAt).FirstOrDefault();
                if (oldest is null) throw new InvalidOperationException("The background operation queue is full. Try again after work finishes.");
                entries.Remove(oldest.Snapshot.Id);
            }
            var entry = new Entry(new(id, kind, BackgroundOperationState.Queued, DateTimeOffset.UtcNow, null, null, null, null), fingerprint);
            entries.Add(id, entry);
            entry.Work = Task.Run(() => RunAsync(entry, run));
            return entry.Snapshot;
        }
    }

    public BackgroundOperation? Find(Guid id)
    {
        lock (sync) return entries.TryGetValue(id, out var entry) ? entry.Snapshot : null;
    }

    private async Task RunAsync(Entry entry, Func<Action<OperationProgress>, CancellationToken, Task<object>> run)
    {
        var acquired = false;
        try
        {
            await workers.WaitAsync(lifetime.Token);
            acquired = true;
            lock (sync)
            {
                // A released slot may race shutdown cancellation. Never start queued work after disposal begins.
                if (disposed) throw new OperationCanceledException(lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                entry.Snapshot = entry.Snapshot with { State = BackgroundOperationState.Running };
            }
            var result = await run(progress =>
            {
                if (progress is null || string.IsNullOrWhiteSpace(progress.Message) || progress.Percent is < 0 or > 100)
                    throw new ArgumentException("Operation progress is invalid.");
                Update(entry, s => s.FinishedAt.HasValue ? s : s with { Progress = progress });
            }, lifetime.Token);
            var data = JsonSerializer.SerializeToElement(result);
            Update(entry, s => s with { State = BackgroundOperationState.Succeeded, Result = data, FinishedAt = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested || disposed)
        { Update(entry, s => s with { State = BackgroundOperationState.Canceled, FinishedAt = DateTimeOffset.UtcNow }); }
        catch (Exception error)
        { Update(entry, s => s with { State = BackgroundOperationState.Failed, Error = error.Message, FinishedAt = DateTimeOffset.UtcNow }); }
        finally
        {
            if (acquired) workers.Release();
            if (recordCompletion is not null)
            {
                BackgroundOperation snapshot;
                lock (sync) snapshot = entry.Snapshot;
                try { await recordCompletion(snapshot); }
                catch (Exception error) { Update(entry, s => s with { ReportingError = error.Message }); }
            }
        }
    }

    private void Update(Entry entry, Func<BackgroundOperation, BackgroundOperation> change)
    { lock (sync) entry.Snapshot = change(entry.Snapshot); }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            pending = entries.Values.Select(e => e.Work).ToArray();
        }
        await lifetime.CancelAsync();
        await Task.WhenAll(pending);
        workers.Dispose();
        lifetime.Dispose();
    }
}
