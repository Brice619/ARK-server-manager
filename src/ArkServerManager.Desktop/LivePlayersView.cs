using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private sealed class PlayerObservationView
    {
        public bool Busy;
        public ServerPlayerQueryResult? Result;
        public string Notice = "Refresh to query this server's online players.";
    }
    private readonly Dictionary<Guid, PlayerObservationView> playerObservations = [];

    private async Task LoadPlayersAsync(Guid serverId)
    {
        if (client is null || !playerObservations.TryGetValue(serverId, out var view) || view.Busy) return;
        view.Busy = true; view.Result = null; view.Notice = "Checking the server's administration connection…"; Render();
        try
        {
            var result = await client.CallAsync<ServerPlayerQueryResult>("server-players-read", new ServerPlayerQueryRequest(serverId),
                TimeSpan.FromSeconds(8), lifetime.Token);
            if (result.ServerId != serverId || result.MeasuredAt == default || result.Players is null
                || result.Players.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.AccountId)))
                throw new InvalidDataException("Invalid player observation.");
            view.Result = result; view.Notice = "Player response received.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException
            or InvalidOperationException or System.Text.Json.JsonException)
        { view.Result = null; view.Notice = "Player list unavailable. Check that the server is running with administration enabled, then refresh."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
