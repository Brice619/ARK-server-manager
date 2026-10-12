using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class PersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ark-manager-test-" + Guid.NewGuid().ToString("N"));
    private string DocumentPath => Path.Combine(directory, "catalog.json");

    [Fact]
    public async Task SavesAndReloadsCompleteDocumentUnderConcurrentWrites()
    {
        var store = new AtomicJsonStore<List<int>>(DocumentPath);
        Assert.Null(await store.LoadAsync());
        await Task.WhenAll(Enumerable.Range(0, 25).Select(n => store.SaveAsync([n, n + 1])));
        var values = await store.LoadAsync();
        Assert.NotNull(values);
        Assert.Equal(2, values.Count);
        Assert.Equal(values[0] + 1, values[1]);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task CanceledWritePreservesPreviousDocument()
    {
        var store = new AtomicJsonStore<List<int>>(DocumentPath);
        await store.SaveAsync([42]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync([99], cancellation.Token));
        Assert.Equal(new[] { 42 }, await store.LoadAsync());
    }

    [Fact]
    public async Task CorruptFileIsReportedWithoutOverwritingIt()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(DocumentPath, "{broken");
        var store = new AtomicJsonStore<List<int>>(DocumentPath);
        await Assert.ThrowsAsync<JsonException>(() => store.LoadAsync());
        Assert.Equal("{broken", await File.ReadAllTextAsync(DocumentPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
