using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;
public sealed class DailyRestartSchedulesTests
{
    [Fact]
    public async Task DailyAttemptSurvivesReloadAndFailedRestartDoesNotRepeatOrBlockSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-daily-restart-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
            var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
            await repository.ChangeAsync(c =>
            {
                for (var i = 0; i < 2; i++) c.AddServer(new(ids[i], ArkEdition.ASE, "TheIsland", Path.Combine(root, "server-" + i),
                    new() { Name = "Fixture " + i, GamePort = 7777 + i * 10, QueryPort = 27015 + i, RconPort = 32330 + i }));
            });
            var file = Path.Combine(root, "schedules.json"); var schedules = new DailyRestartSchedules(repository, file);
            Assert.False((await schedules.ReadAsync(ids[0])).Enabled);
            foreach (var id in ids) await schedules.SaveAsync(id, new(4, 0), true, true);
            var called = new List<Guid>(); var notices = new List<string>();
            Task<LifecycleOperationResult> Restart(Guid id, CancellationToken ct)
            {
                called.Add(id);
                if (id == ids[0]) throw new IOException("fixture restart failure");
                return Task.FromResult(new LifecycleOperationResult([new(id, ServerState.Running, true, null)]));
            }
            Task Report(Guid id, string message, CancellationToken ct) { notices.Add(message); return Task.CompletedTask; }
            var due = new DateTimeOffset(2026, 10, 12, 4, 0, 10, TimeSpan.Zero);
            await schedules.CycleAsync(due.AddMinutes(-1), TimeZoneInfo.Utc, Restart, Report); Assert.Empty(called);
            await schedules.CycleAsync(due, TimeZoneInfo.Utc, Restart, Report); Assert.Equal(ids, called);
            Assert.Contains(notices, notice => notice.Contains("failure"));
            await new DailyRestartSchedules(repository, file).CycleAsync(due.AddSeconds(20), TimeZoneInfo.Utc, Restart, Report);
            Assert.Equal(2, called.Count);
            await schedules.CycleAsync(due.AddDays(1), TimeZoneInfo.Utc, Restart, Report); Assert.Equal(4, called.Count);
            var valid = await File.ReadAllTextAsync(file);
            var beforeProblems = notices.Count;
            await File.WriteAllTextAsync(file, "{\"SchemaVersion\":99,\"Schedules\":[]}");
            await schedules.CycleAsync(due.AddDays(1), TimeZoneInfo.Utc, Restart, Report);
            Assert.Equal(beforeProblems + 2, notices.Count);
            await schedules.CycleAsync(due.AddDays(1), TimeZoneInfo.Utc, Restart, Report);
            Assert.Equal(beforeProblems + 2, notices.Count); Assert.Equal(4, called.Count);
            await File.WriteAllTextAsync(file, valid);
            await schedules.CycleAsync(due.AddDays(1), TimeZoneInfo.Utc, Restart, Report);
            await File.WriteAllTextAsync(file, "{\"SchemaVersion\":99,\"Schedules\":[]}");
            await schedules.CycleAsync(due.AddDays(1), TimeZoneInfo.Utc, Restart, Report);
            Assert.Equal(beforeProblems + 4, notices.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
