using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool reviewLivePlayers;
    private sealed class PlayerObservationView
    {
        public bool Busy;
        public ServerPlayerQueryResult? Result;
        public string Notice = "Refresh to query this server's online players.";
    }
    private readonly Dictionary<Guid, PlayerObservationView> playerObservations = [];

    private void RenderLivePlayers(ServerDefinition server)
    {
        if (!playerObservations.TryGetValue(server.Id, out var view))
            playerObservations[server.Id] = view = new();
        content.Children.Add(Label("Players", 32, true));
        content.Children.Add(Label("Players reported by this server's administration connection.", 17));
        content.Children.Add(ActionButton(view.Busy ? "Checking players…" : "Refresh players",
            client is null || view.Busy ? null : () => { _ = LoadPlayersAsync(server.Id); }));
        var search = new TextBox { Text = playerFilter, PlaceholderText = "Search reported players…",
            MaxWidth = 560, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            FontSize = 16, Padding = new Thickness(12, 10) };
        Avalonia.Automation.AutomationProperties.SetName(search, "Search reported players");
        content.Children.Add(search);
        var rows = new StackPanel { Spacing = 14 };
        void Populate()
        {
            rows.Children.Clear();
            var matches = view.Result?.Players.Where(p => p.Name.Contains(playerFilter, StringComparison.OrdinalIgnoreCase)
                || p.AccountId.Contains(playerFilter, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            foreach (var player in matches)
            {
                var row = new StackPanel { Spacing = 5 };
                var name = Label(player.Name, 18, true); name.TextWrapping = TextWrapping.Wrap; row.Children.Add(name);
                var account = Label(player.AccountId, 14); account.Foreground = Muted; row.Children.Add(account);
                rows.Children.Add(row);
            }
            if (matches.Length == 0) rows.Children.Add(Label(view.Result is null ? "Player list unavailable"
                : view.Result.Players.Count == 0 && view.Result.CompleteListConfirmed ? "No players connected"
                : "No matching reported players", 20, true));
        }
        search.TextChanged += (_, _) => { playerFilter = search.Text ?? ""; Populate(); };
        Populate(); content.Children.Add(Surface(rows));
        var notice = Label(view.Notice, 14); notice.TextWrapping = TextWrapping.Wrap; notice.Foreground = Muted;
        content.Children.Add(notice);
        RenderAnnouncementAction(server);
        if (view.Result is { } result)
        {
            var observed = Label($"Checked {result.MeasuredAt.LocalDateTime:t}. " + (result.CompleteListConfirmed
                ? "The server reported no connected players." : "Reported entries are shown; completeness is not yet confirmed. Refresh for a new observation."), 13);
            observed.TextWrapping = TextWrapping.Wrap; observed.Foreground = Muted; content.Children.Add(observed);
        }
    }

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
