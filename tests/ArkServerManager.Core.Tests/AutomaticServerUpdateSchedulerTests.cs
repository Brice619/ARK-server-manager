using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class AutomaticServerUpdateSchedulerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-auto-update-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task FailedMapDoesNotPreventOtherChecksAndRepeatedNoticesAreSuppressed()
    {
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        await repository.ChangeAsync(c =>
        {
            for (var i = 0; i < ids.Length; i++)
                c.AddServer(new(ids[i], ArkEdition.ASE, "TheIsland", Path.Combine(root, i.ToString()),
                    new() { Name = "Fixture", GamePort = 7777 + i * 10, QueryPort = 27015 + i * 10, RconPort = 32330 + i * 10 }));
        });
        foreach (var i in new[] { 0, 1 })
        {
            var folder = Path.Combine(root, i.ToString(), "steamapps"); Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "appmanifest_376030.acf"), "eligibility fixture");
        }
        var checkedIds = new List<Guid>(); var notices = new List<Guid>();
        var scheduler = new AutomaticServerUpdateScheduler(repository, (id, _) =>
        {
            checkedIds.Add(id);
            if (id == ids[0]) throw new IOException("Fixture failure");
            return Task.FromResult(new ServerUpdateCheckResult(id, 1, 2, new(false, true, false, "Queued")));
        }, (id, _, _) => { notices.Add(id); return Task.CompletedTask; });
        await scheduler.CycleAsync(default); await scheduler.CycleAsync(default);
        Assert.Equal(new[] { ids[0], ids[1], ids[0], ids[1] }, checkedIds);
        Assert.Equal(new[] { ids[0], ids[1] }, notices);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
