using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class BackgroundActivityRecorderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-job-activity-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("server-lifecycle")]
    [InlineData("server-member-lifecycle")]
    [InlineData("server-automatic-startup")]
    public async Task CompletedClusterJobRecordsMemberFailureAsError(string kind)
    {
        var failed = Guid.NewGuid();
        var result = new LifecycleOperationResult([
            new(Guid.NewGuid(), ServerState.Running, true, null),
            new(failed, ServerState.Failed, false, "Startup failed")]);
        var operation = new BackgroundOperation(Guid.NewGuid(), kind, BackgroundOperationState.Succeeded,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, JsonSerializer.SerializeToElement(result), null);
        var journal = new ActivityJournal(Path.Combine(root, "activity.jsonl"));
        await BackgroundActivityRecorder.RecordAsync(journal, operation);
        var entries = (await journal.ReadRecentAsync(10)).Entries;
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(ActivitySeverity.Error, entry.Severity));
        Assert.Contains(entries, e => e.ServerId == failed && e.Message == "Startup failed" && e.OperationId == operation.Id);
        Assert.Contains(entries, e => e.Message.Contains("1 of 2"));
    }

    [Fact]
    public async Task MissingMemberResultsNeverProducesSuccessNotice()
    {
        var operation = new BackgroundOperation(Guid.NewGuid(), "server-shutdown", BackgroundOperationState.Succeeded,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null);
        var journal = new ActivityJournal(Path.Combine(root, "activity.jsonl"));
        await BackgroundActivityRecorder.RecordAsync(journal, operation);
        Assert.Equal(ActivitySeverity.Error, Assert.Single((await journal.ReadRecentAsync(10)).Entries).Severity);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
