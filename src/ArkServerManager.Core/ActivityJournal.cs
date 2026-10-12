using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace ArkServerManager.Core;

public enum ActivitySeverity { Information, Warning, Error }
public sealed record ActivityEntry(Guid Id, DateTimeOffset Time, string Category, ActivitySeverity Severity,
    string Message, Guid? ServerId = null, Guid? OperationId = null, string? Stage = null, int? ExitCode = null);
public sealed record ActivityHistory(IReadOnlyList<ActivityEntry> Entries, bool OlderEntriesOmitted, int UnreadableEntries);

/// <summary>Durable activity summaries. Reads are bounded; records are not deleted by this component.</summary>
public sealed class ActivityJournal
{
    private const int MaximumEntryBytes = 64 * 1024;
    private const int ReadWindowBytes = 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly UTF8Encoding Encoding = new(false, true);
    private readonly string path;
    private readonly SemaphoreSlim gate;

    public ActivityJournal(string path)
    {
        this.path = Path.GetFullPath(path);
        gate = Gates.GetOrAdd(this.path, _ => new(1, 1));
    }

    public Task RecordIncidentAsync(Guid serverId, ServerIncident incident, CancellationToken ct = default)
    {
        if (serverId == Guid.Empty) throw new ArgumentException("A server identity is required.");
        return AppendAsync(new(Guid.NewGuid(), incident.Time, "server", ActivitySeverity.Error,
            incident.Message, serverId, Stage: incident.Stage, ExitCode: incident.ExitCode), ct);
    }

    public async Task AppendAsync(ActivityEntry entry, CancellationToken ct = default)
    {
        Validate(entry);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
        if (bytes.Length > MaximumEntryBytes) throw new ArgumentException("The activity entry exceeds the supported size.");
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            // Preserve an interrupted record, but separate it from the new complete record.
            if (file.Length > 0)
            {
                file.Seek(-1, SeekOrigin.End);
                if (file.ReadByte() != '\n')
                {
                    file.Seek(0, SeekOrigin.End);
                    await file.WriteAsync(new byte[] { (byte)'\n' }, ct);
                }
            }
            file.Seek(0, SeekOrigin.End);
            await file.WriteAsync(bytes, ct);
            await file.WriteAsync(new byte[] { (byte)'\n' }, ct);
            await file.FlushAsync(ct);
            file.Flush(true);
        }
        finally { gate.Release(); }
    }

    public async Task<ActivityHistory> ReadRecentAsync(int limit, Guid? serverId = null, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await gate.WaitAsync(ct);
        try
        {
            FileStream file;
            try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            { return new([], false, 0); }
            await using var ownedFile = file;
            var length = file.Length;
            var offset = Math.Max(0, length - ReadWindowBytes);
            file.Position = offset;
            var bytes = new byte[checked((int)(length - offset))];
            await file.ReadExactlyAsync(bytes, ct);
            var start = offset > 0 ? Array.IndexOf(bytes, (byte)'\n') + 1 : 0;
            if (offset > 0 && start == 0) return new([], true, 1);
            var entries = new List<ActivityEntry>();
            var unreadable = 0;
            for (var i = start; i < bytes.Length;)
            {
                var end = Array.IndexOf(bytes, (byte)'\n', i);
                if (end < 0) { unreadable++; break; }
                if (end == i) { i = end + 1; continue; }
                try
                {
                    var text = Encoding.GetString(bytes, i, end - i);
                    var entry = JsonSerializer.Deserialize<ActivityEntry>(text)
                        ?? throw new InvalidDataException("Empty activity entry.");
                    Validate(entry);
                    if (serverId is null || entry.ServerId == serverId) entries.Add(entry);
                }
                catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException)
                { unreadable++; }
                i = end + 1;
            }
            var omitted = offset > 0 || entries.Count > limit;
            return new(entries.TakeLast(limit).Reverse().ToArray(), omitted, unreadable);
        }
        finally { gate.Release(); }
    }

    private static void Validate(ActivityEntry entry)
    {
        if (entry is null || entry.Id == Guid.Empty || entry.Time == default || string.IsNullOrWhiteSpace(entry.Category)
            || string.IsNullOrWhiteSpace(entry.Message) || !Enum.IsDefined(entry.Severity))
            throw new ArgumentException("The activity entry is invalid.");
    }
}
