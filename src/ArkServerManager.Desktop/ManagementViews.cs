using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private int managementTab;
    private static string MapName(string map) => ServerMapCatalog.DisplayName(map);
    private static string? MapArt(string map) => map.Contains("fjord", StringComparison.OrdinalIgnoreCase) ? "fjord"
        : map.Contains("aberration", StringComparison.OrdinalIgnoreCase) ? "cavern-v1"
        : map.Contains("extinction", StringComparison.OrdinalIgnoreCase) || map.Contains("genesis", StringComparison.OrdinalIgnoreCase) || map.Equals("Gen2", StringComparison.OrdinalIgnoreCase) ? "ruined-city-v1"
        : map.Contains("valguero", StringComparison.OrdinalIgnoreCase) || map.Contains("colony", StringComparison.OrdinalIgnoreCase) ? "fjord"
        : map.Contains("center", StringComparison.OrdinalIgnoreCase) || map.Contains("crystal", StringComparison.OrdinalIgnoreCase) || map.Contains("astraeos", StringComparison.OrdinalIgnoreCase) ? "island"
        : map.Contains("ragnarok", StringComparison.OrdinalIgnoreCase) || map.Contains("scorched", StringComparison.OrdinalIgnoreCase) ? "canyon"
        : map.Contains("island", StringComparison.OrdinalIgnoreCase) ? "island" : null;

    internal static Border Surface(Control child, Thickness? padding = null) => new()
    {
        Background = Brush.Parse("#17252F"), BorderBrush = Brush.Parse("#2B3A46"),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
        Padding = padding ?? new Thickness(24), Child = child
    };

    internal static Button ActionButton(string title, Action? action = null, bool primary = false)
    {
        var button = new Button { Content = title, FontSize = 15, Padding = new Thickness(18, 12),
            CornerRadius = new CornerRadius(5), BorderBrush = Brush.Parse("#405363"), BorderThickness = new Thickness(1),
            Background = primary ? Amber : Brush.Parse("#1B2934"), Foreground = primary ? Brush.Parse("#181B1F") : Brushes.White };
        button.IsEnabled = action is not null;
        if (primary) button.Classes.Add("accent");
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }

    internal static Control MapPicture(string map, double width, double height)
    {
        var key = MapArt(map);
        return new Border { Width = width, Height = height, CornerRadius = new CornerRadius(5), ClipToBounds = true,
            Background = Brush.Parse("#101C26"),
            Child = key is not null ? new Image { Source = LoadArtwork(key), Stretch = Stretch.UniformToFill }
                : VectorIcon(BoxIcon, Muted, 48) };
    }

    private void RenderServerDetails(ServerDefinition server)
    {
        if (updateViewServer == server.Id) { RenderServerUpdate(server); return; }
        if (scheduleServer == server.Id) { RenderRestartSchedule(server); return; }
        if (presetApplicationServer == server.Id) { RenderPresetApplication(server); return; }
        if (managementTab == 1 && advancedEditorServer == server.Id)
        { RenderAdvancedEditor(server); return; }
        if (managementTab == 2 && modPageServer == server.Id && configuredMod is { } configurationMod)
        { RenderModConfiguration(server, configurationMod); return; }
        if (managementTab == 3 && backupViewServer == server.Id && selectedRestorePoint is not null)
        { RenderServerBackups(server); return; }
        if (managementTab is 1 or 4 or 5 && Height < 760)
        {
            content.Children.Add(ActionButton("← Back to dashboard", () => { selectedServer = null; Render(); }));
            content.Children.Add(Label(server.Settings.Name + " · " + server.Edition + " · " + MapName(server.Map), 23, true));
        }
        else ApprovedDetailHeader(server.Settings.Name, server.Edition, server.Settings, server: server);
        ApprovedTabs(["Overview", "Settings", "Mods", "Backups", "Players", "Console"]);
        if (managementTab == 0) { ApprovedServerOverview(server); return; }
        if (managementTab == 1) { ApprovedServerSettings(server); return; }
        if (managementTab == 2) { RenderServerMods(server); return; }
        if (managementTab == 3) { ApprovedBackups(server); return; }
        if (managementTab == 4) { ApprovedPlayers(server); return; }
        if (managementTab == 5) { ApprovedConsole(server); return; }
    }

    private static void Fact(StackPanel body, string name, string value)
    {
        var line = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 10) };
        var caption = Label(name, 14); caption.Foreground = Muted; line.Children.Add(caption);
        line.Children.Add(Label(value, 17)); body.Children.Add(line);
    }

    private int settingsCategory;
    private static void ReadOnlyField(StackPanel body, string title, string value)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };
        var label = Label(title, 16); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
        var field = new TextBox { Text = value, IsReadOnly = true, MinWidth = 320, FontSize = 16,
            Background = Brush.Parse("#101C26"), Padding = new Thickness(12, 10) };
        Avalonia.Automation.AutomationProperties.SetName(field, title);
        Grid.SetColumn(field, 1); row.Children.Add(field); body.Children.Add(row);
    }

    private void RenderClusterDetails(ClusterDefinition cluster) => ApprovedClusterDetails(cluster);
}
