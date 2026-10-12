namespace ArkServerManager.Core;

public sealed class AutomaticServerUpdateScheduler(CatalogRepository repository,
    Func<Guid, CancellationToken, Task<ServerUpdateCheckResult>> check,
    Func<Guid, string, CancellationToken, Task> report)
{
    private readonly Dictionary<Guid, string> notices = [];
    private string? catalogProblem;
    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { await CycleAsync(ct); catalogProblem = null; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException
                or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            {
                if (catalogProblem != error.Message)
                    await report(Guid.Empty, "Automatic server update checks need attention: " + error.Message, ct);
                catalogProblem = error.Message;
            }
            await Task.Delay(interval, ct);
        }
    }

    public async Task CycleAsync(CancellationToken ct)
    {
        var catalog = await repository.ReadAsync(ct);
        foreach (var server in catalog.Servers)
        {
            ct.ThrowIfCancellationRequested();
            // Imported installations without a Steam manifest require an explicit update review.
            if (!File.Exists(Path.Combine(server.Directory, "steamapps", "appmanifest_" + SteamCmdInstaller.AppId(server.Edition) + ".acf"))) continue;
            try
            {
                var result = await check(server.Id, ct);
                var message = result.Result.Queued ? "Server update available; queued until this map stops. Build " + result.AvailableBuild
                    : result.Result.Updated ? "Server updated to build " + result.AvailableBuild : null;
                if (message is null) notices.Remove(server.Id);
                else if (notices.GetValueOrDefault(server.Id) != message)
                { await report(server.Id, message, ct); notices[server.Id] = message; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException
                or UnauthorizedAccessException or ArgumentException or TimeoutException or HttpRequestException)
            {
                var message = "Server update check needs attention: " + error.Message;
                if (notices.GetValueOrDefault(server.Id) != message)
                { await report(server.Id, message, ct); notices[server.Id] = message; }
            }
        }
    }
}
