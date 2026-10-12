using System.Text;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ActivityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-activity-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(root, "activity.jsonl");
    private ActivityJournal Journal => new(FilePath);
    private static ActivityEntry Entry(string message, Guid? server = null) => new(Guid.NewGuid(), DateTimeOffset.UtcNow,
        "test", ActivitySeverity.Information, message, server);

    [Fact]
    public async Task PersistsConcurrentEntriesAndReturnsRecentServerActivityAfterReload()
    {
        var server = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Journal.AppendAsync(Entry("Entry " + i, server))));
        var latest = Entry("Latest", server);
        await Journal.AppendAsync(latest);
        await Journal.AppendAsync(Entry("Other server", Guid.NewGuid()));
        var history = await Journal.ReadRecentAsync(5, server);
        Assert.Equal(5, history.Entries.Count);
        Assert.Equal(latest.Id, history.Entries[0].Id);
        Assert.True(history.OlderEntriesOmitted);
        Assert.Equal(0, history.UnreadableEntries);
        Assert.Equal(22, (await Journal.ReadRecentAsync(100)).Entries.Count);
    }

    [Fact]
    public async Task InterruptedTailDoesNotHideValidHistoryOrMergeNextEntry()
    {
        var first = Entry("First");
        await Journal.AppendAsync(first);
        await File.AppendAllTextAsync(FilePath, "{\"unfinished\":", Encoding.UTF8);
        var partial = await Journal.ReadRecentAsync(100);
        Assert.Equal(first.Id, Assert.Single(partial.Entries).Id);
        Assert.Equal(1, partial.UnreadableEntries);
        var next = Entry("Next");
        await Journal.AppendAsync(next);
        var history = await Journal.ReadRecentAsync(100);
        Assert.Equal(new[] { next.Id, first.Id }, history.Entries.Select(e => e.Id));
        Assert.Equal(1, history.UnreadableEntries);
    }

    [Fact]
    public async Task BackgroundCompletionIsDurableAndJournalFailureDoesNotChangeWorkOutcome()
    {
        var id = Guid.NewGuid();
        await using (var operations = new BackgroundOperations(4, 1,
            operation => Journal.AppendAsync(Entry(operation.State.ToString()) with { OperationId = operation.Id })))
        {
            operations.Submit(id, "test", null, (_, _) => Task.FromResult<object>(new { }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((await Journal.ReadRecentAsync(100)).Entries.Count == 0) await Task.Delay(10, timeout.Token);
        }
        Assert.Equal(id, Assert.Single((await Journal.ReadRecentAsync(100)).Entries).OperationId);
        var reporting = new BackgroundOperations(4, 1, _ => throw new IOException("Log is unavailable"));
        var job = reporting.Submit(Guid.NewGuid(), "test", null, (_, _) => Task.FromResult<object>(new { }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (reporting.Find(job.Id)!.ReportingError is null) await Task.Delay(10, deadline.Token);
            Assert.Equal(BackgroundOperationState.Succeeded, reporting.Find(job.Id)!.State);
            Assert.Equal("Log is unavailable", reporting.Find(job.Id)!.ReportingError);
        }
        finally { await reporting.DisposeAsync(); }
    }

    [Fact]
    public async Task BoundedReadSkipsInvalidOldBytesAndStillReturnsNewestValidRecord()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(FilePath, Enumerable.Repeat((byte)0xff, 2 * 1024 * 1024).ToArray());
        var latest = Entry("Newest");
        await Journal.AppendAsync(latest);
        var history = await Journal.ReadRecentAsync(100);
        Assert.True(history.OlderEntriesOmitted);
        Assert.Equal(latest.Id, Assert.Single(history.Entries).Id);
    }

    [Fact]
    public async Task UnreadableJournalPathDoesNotAppearAsEmptySuccessfulHistory()
    {
        Assert.Empty((await Journal.ReadRecentAsync(100)).Entries);
        Directory.CreateDirectory(FilePath);
        var error = await Record.ExceptionAsync(() => Journal.ReadRecentAsync(100));
        Assert.True(error is IOException or UnauthorizedAccessException);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
