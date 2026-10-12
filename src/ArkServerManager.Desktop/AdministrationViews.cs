using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private int playerCategory;
    private string playerFilter = "";
    private string consoleFilter = "";
    private bool consolePaused, consoleCleared;
    private Guid? administrationServer;

    private void SelectAdministrationServer(ServerDefinition server)
    {
        if (administrationServer == server.Id) return;
        administrationServer = server.Id; playerCategory = 0; playerFilter = consoleFilter = "";
        consolePaused = consoleCleared = false;
    }

    private void RenderPlayers(ServerDefinition server)
    {
        SelectAdministrationServer(server);
        if (!designPreview || reviewLivePlayers) { RenderLivePlayers(server); return; }
        content.Children.Add(Label("Players", 32, true));
        var subtitle = Label("View and manage players on this server.", 17); subtitle.Foreground = Muted;
        content.Children.Add(subtitle);
        var toolbar = new WrapPanel { Margin = new Thickness(0, 22, 0, 18) };
        var online = ActionButton("Online", () => { playerCategory = 0; Render(); }, playerCategory == 0);
        online.Margin = new Thickness(0, 0, 12, 10); toolbar.Children.Add(online);
        var banned = ActionButton("Banned", () => { playerCategory = 1; Render(); }, playerCategory == 1);
        banned.Margin = new Thickness(0, 0, 28, 10); toolbar.Children.Add(banned);
        var search = new TextBox { Text = playerFilter, PlaceholderText = "Search players…", Width = 430,
            FontSize = 16, Padding = new Thickness(12, 10) };
        Avalonia.Automation.AutomationProperties.SetName(search, "Search players");
        toolbar.Children.Add(search); content.Children.Add(toolbar);
        var rows = new StackPanel { Spacing = 0 };
        void Populate()
        {
            rows.Children.Clear();
            var examples = designPreview && playerCategory == 0 ? new[] { "Survivor", "River", "Stone" } : [];
            foreach (var name in examples.Where(n => n.Contains(playerFilter, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto"), MinHeight = 76 };
                var player = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, VerticalAlignment = VerticalAlignment.Center };
                player.Children.Add(new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(21),
                    Background = Brush.Parse("#2B4355"), Child = VectorIcon("M12,2 A5,5 0 1 1 12,12 A5,5 0 1 1 12,2 M2,23 L2,20 A10,8 0 0 1 22,20 L22,23 Z", Muted, 24) });
                player.Children.Add(Label(name, 18, true)); row.Children.Add(player);
                var platform = Label("Steam · Illustrative player", 16); platform.Foreground = Muted;
                platform.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(platform, 1); row.Children.Add(platform);
                var more = ActionButton("•••", () => { });
                var menu = new ContextMenu();
                foreach (var action in new[] { "Kick player", "Ban player" }) menu.Items.Add(new MenuItem { Header = action, IsEnabled = false });
                more.Click += (_, _) => menu.Open(more); more.VerticalAlignment = VerticalAlignment.Center;
                Avalonia.Automation.AutomationProperties.SetName(more, "More actions for " + name);
                Grid.SetColumn(more, 2); row.Children.Add(more);
                rows.Children.Add(new Border { BorderBrush = Brush.Parse("#2B3A46"), BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(20, 8), Child = row });
            }
            if (rows.Children.Count == 0)
            {
                var empty = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
                empty.Children.Add(Label(designPreview ? playerFilter.Length > 0 ? "No matching players" : "No banned players in this preview"
                    : playerCategory == 0 ? "Live player list unavailable" : "Ban list unavailable", 24, true));
                empty.Children.Add(Label(designPreview ? "These are illustrative lists. No server is running."
                    : "Player queries and administration are still being connected.", 16)); rows.Children.Add(empty);
            }
        }
        search.TextChanged += (_, _) => { playerFilter = search.Text ?? ""; Populate(); };
        Populate(); content.Children.Add(Surface(rows, new Thickness(0)));
        var announce = ActionButton("Send announcement"); announce.Margin = new Thickness(0, 22, 0, 0);
        content.Children.Add(announce);
        var note = Label(designPreview ? "Design preview · Player actions and announcements are disabled."
            : "No player count is inferred from an unavailable connection.", 13); note.Foreground = Muted; content.Children.Add(note);
    }

    private void RenderConsole(ServerDefinition server)
    {
        SelectAdministrationServer(server);
        if (!designPreview || reviewCommandConsole) { RenderCommandConsole(server); return; }
        content.Children.Add(Label("Console", 32, true));
        var subtitle = Label("Server output and administrator commands.", 17); subtitle.Foreground = Muted;
        content.Children.Add(subtitle);
        var body = new StackPanel { Spacing = 18 };
        body.Children.Add(Status(designPreview ? "Design preview · Not connected" : "Not connected", "#94A7B7"));
        var toolbar = new WrapPanel();
        var pause = ActionButton(consolePaused ? "Resume scroll" : "Pause scroll", designPreview ? () => { consolePaused = !consolePaused; Render(); } : null);
        pause.Margin = new Thickness(0, 0, 12, 10); toolbar.Children.Add(pause);
        var clear = ActionButton("Clear view", designPreview ? () => { consoleCleared = true; Render(); } : null);
        clear.Margin = new Thickness(0, 0, 12, 10); toolbar.Children.Add(clear);
        var find = new TextBox { Text = consoleFilter, PlaceholderText = "Find text…", Width = 400, FontSize = 16, Padding = new Thickness(12, 10) };
        Avalonia.Automation.AutomationProperties.SetName(find, "Find console text"); toolbar.Children.Add(find);
        body.Children.Add(toolbar);
        var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, monospace"), FontSize = 15, MinHeight = 290, MaxHeight = 430,
            Background = Brush.Parse("#0C1720"), Padding = new Thickness(18),
            Foreground = Brush.Parse("#D4E2ED") };
        Avalonia.Automation.AutomationProperties.SetName(output, "Server console output");
        var examples = new[] { "[Preview 14:02] Server startup complete.", "[Preview 14:03] Player Survivor joined.",
            "[Preview 14:14] World saved.", "[Preview 14:20] Player River joined." };
        void Filter()
        {
            output.Text = !designPreview ? "Live server output is unavailable."
                : consoleCleared ? "The preview view was cleared. No log files were deleted."
                : string.Join(Environment.NewLine, examples.Where(line => line.Contains(consoleFilter, StringComparison.OrdinalIgnoreCase)));
        }
        find.TextChanged += (_, _) => { consoleFilter = find.Text ?? ""; Filter(); };
        Filter(); body.Children.Add(output);
        var commandRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var command = new TextBox { PlaceholderText = "Enter an admin command…", IsEnabled = false, FontSize = 16, Padding = new Thickness(12, 10) };
        Avalonia.Automation.AutomationProperties.SetName(command, "Administrator command");
        commandRow.Children.Add(command); var send = ActionButton("Send"); send.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(send, 1); commandRow.Children.Add(send); body.Children.Add(commandRow);
        var note = Label("Commands and live output are still being connected. Clear view never deletes log files.", 14);
        note.Foreground = Muted; body.Children.Add(note); content.Children.Add(Surface(body));
    }
}

