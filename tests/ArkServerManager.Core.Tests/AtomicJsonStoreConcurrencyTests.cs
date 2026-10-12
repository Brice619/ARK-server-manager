using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class AtomicJsonStoreConcurrencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-atomic-read-" + Guid.NewGuid().ToString("N"));
    public sealed record Document(int Revision, string Contents);

    [Fact]
    public async Task PublicationCanReplaceRecordWhilePreviousSnapshotIsStillOpen()
    {
        var path = Path.Combine(root, "record.json"); var store = new AtomicJsonStore<Document>(path);
        await store.SaveAsync(new(1, "original"));
        await using var previous = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await store.SaveAsync(new(2, "updated"));
        var held = await System.Text.Json.JsonSerializer.DeserializeAsync<Document>(previous);
        Assert.Equal(new(1, "original"), held);
        Assert.Equal(new(2, "updated"), await store.LoadAsync());
    }

    [Fact]
    public async Task IndependentReadersObserveCompleteRecordsDuringRepeatedPublication()
    {
        var path = Path.Combine(root, "record.json"); var writer = new AtomicJsonStore<Document>(path);
        var contents = new string('x', 256 * 1024);
        await writer.SaveAsync(new(0, contents));
        var readers = Enumerable.Range(0, 3).Select(async _ =>
        {
            var reader = new AtomicJsonStore<Document>(path);
            for (var count = 0; count < 40; count++)
            {
                var value = await reader.LoadAsync();
                Assert.NotNull(value); Assert.InRange(value.Revision, 0, 20); Assert.Equal(contents, value.Contents);
            }
        }).ToArray();
        for (var revision = 1; revision <= 20; revision++) await writer.SaveAsync(new(revision, contents));
        await Task.WhenAll(readers);
        Assert.Equal(20, (await writer.LoadAsync())!.Revision);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
